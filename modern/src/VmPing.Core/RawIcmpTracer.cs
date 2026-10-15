using System.Buffers.Binary;
using System.Diagnostics;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
namespace VmPing.Core;

// Parallel TTL probing over a raw ICMP socket (Linux with root or CAP_NET_RAW). Unlike System.Net.Ping, every
// reply is matched by the identifier and sequence number of the original echo request, including the copy that
// routers embed in TTL-exceeded and unreachable messages, so concurrent probes never get mixed up.
public sealed class RawIcmpTracer : IDisposable
{
    private const int PayloadSize = 24;
    private readonly Socket socket;
    private readonly AddressFamily family;
    // One identifier for the whole trace keeps a single ICMP session in NAT/CGNAT boxes, which key ICMP state by
    // identifier and limit how many new sessions a subscriber opens. The sequence carries cycle (high) + TTL (low).
    private readonly ushort identifier = (ushort)Random.Shared.Next(1, ushort.MaxValue);
    private ushort cycle;
    public TimeSpan SendGap { get; set; } = TimeSpan.FromMilliseconds(20);
    private RawIcmpTracer(AddressFamily family)
    {
        this.family = family;
        socket = new Socket(family, SocketType.Raw, family == AddressFamily.InterNetwork ? ProtocolType.Icmp : ProtocolType.IcmpV6);
        // ReceiveFrom on a socket that has not sent yet requires an explicit bind.
        socket.Bind(new IPEndPoint(family == AddressFamily.InterNetwork ? IPAddress.Any : IPAddress.IPv6Any, 0));
    }
    public static RawIcmpTracer? TryCreate(AddressFamily family)
    {
        if (!OperatingSystem.IsLinux()) return null;
        try { return new RawIcmpTracer(family); }
        catch (SocketException) { return null; }
    }
    public void Dispose() => socket.Dispose();
    public async Task<HopResult[]> ProbeAsync(IPAddress destination, int maxTtl, int timeoutMs, CancellationToken token)
    {
        // Late replies from an earlier cycle carry an older sequence number and are discarded.
        cycle = (ushort)((cycle + 1) & 0xFF);
        var sent = new long[maxTtl + 1];
        var results = new HopResult?[maxTtl + 1];
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(timeoutMs);
        var receiving = ReceiveAsync(destination, sent, results, maxTtl, timeout.Token);
        var endpoint = new IPEndPoint(destination, 0);
        for (var ttl = 1; ttl <= maxTtl; ttl++)
        {
            // Sends are sequential on this thread, so setting the TTL right before each send is race-free.
            if (family == AddressFamily.InterNetwork) socket.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.IpTimeToLive, ttl);
            else socket.SetSocketOption(SocketOptionLevel.IPv6, SocketOptionName.IpTimeToLive, ttl);
            sent[ttl] = Stopwatch.GetTimestamp();
            try { await socket.SendToAsync(BuildRequest((ushort)((cycle << 8) | ttl)), SocketFlags.None, endpoint, token); }
            catch (SocketException ex) { throw new InvalidOperationException($"Falha ao enviar sondagem: {ex.Message}", ex); }
            // Pacing avoids ICMP burst limits on routers and CGNAT (similar to mtr); 30 hops take ~600 ms to send.
            if (ttl < maxTtl && SendGap > TimeSpan.Zero) await Task.Delay(SendGap, token);
        }
        try { await receiving; }
        catch (OperationCanceledException) when (!token.IsCancellationRequested) { }
        token.ThrowIfCancellationRequested();
        return Enumerable.Range(1, maxTtl).Select(ttl => results[ttl] ?? new HopResult(ttl, null, "", null, IPStatus.TimedOut)).ToArray();
    }
    private async Task ReceiveAsync(IPAddress destination, long[] sent, HopResult?[] results, int maxTtl, CancellationToken token)
    {
        var buffer = new byte[1500];
        EndPoint any = new IPEndPoint(family == AddressFamily.InterNetwork ? IPAddress.Any : IPAddress.IPv6Any, 0);
        while (!Complete(results, maxTtl))
        {
            var received = await socket.ReceiveFromAsync(buffer, SocketFlags.None, any, token);
            var now = Stopwatch.GetTimestamp();
            if (Parse(buffer.AsSpan(0, received.ReceivedBytes), out var sequence, out var status) is false) continue;
            var ttl = sequence & 0xFF;
            if (sequence >> 8 != cycle || ttl < 1 || ttl > maxTtl || results[ttl] is not null) continue;
            var from = ((IPEndPoint)received.RemoteEndPoint).Address;
            if (status == IPStatus.Success && !from.Equals(destination)) continue;
            var milliseconds = Stopwatch.GetElapsedTime(sent[ttl], now).TotalMilliseconds;
            results[ttl] = new HopResult(ttl, from, "", status is IPStatus.Success or IPStatus.TtlExpired ? milliseconds : null, status);
        }
    }
    // Done when every TTL up to the first one that reached the destination has an answer.
    private static bool Complete(HopResult?[] results, int maxTtl)
    {
        var last = maxTtl;
        for (var ttl = 1; ttl <= maxTtl; ttl++) if (results[ttl]?.Status == IPStatus.Success) { last = ttl; break; }
        for (var ttl = 1; ttl <= last; ttl++) if (results[ttl] is null) return false;
        return true;
    }
    private byte[] BuildRequest(ushort sequence)
    {
        var packet = new byte[8 + PayloadSize];
        packet[0] = family == AddressFamily.InterNetwork ? (byte)8 : (byte)128;
        BinaryPrimitives.WriteUInt16BigEndian(packet.AsSpan(4), identifier);
        BinaryPrimitives.WriteUInt16BigEndian(packet.AsSpan(6), sequence);
        "vmPing-modern-trace-----"u8.CopyTo(packet.AsSpan(8));
        // The kernel fills in the ICMPv6 checksum (it needs the pseudo-header); ICMPv4 is ours to compute.
        if (family == AddressFamily.InterNetwork) BinaryPrimitives.WriteUInt16BigEndian(packet.AsSpan(2), Checksum(packet));
        return packet;
    }
    public static ushort Checksum(ReadOnlySpan<byte> data)
    {
        uint sum = 0;
        for (var i = 0; i + 1 < data.Length; i += 2) sum += BinaryPrimitives.ReadUInt16BigEndian(data[i..]);
        if (data.Length % 2 == 1) sum += (uint)data[^1] << 8;
        while (sum >> 16 != 0) sum = (sum & 0xFFFF) + (sum >> 16);
        return (ushort)~sum;
    }
    // IPv4 raw sockets deliver the IP header; IPv6 raw sockets deliver only the ICMPv6 message.
    private bool Parse(ReadOnlySpan<byte> packet, out int sequence, out IPStatus status)
    {
        sequence = 0; status = IPStatus.Unknown;
        var icmp = packet;
        if (family == AddressFamily.InterNetwork)
        {
            if (packet.Length < 20) return false;
            icmp = packet[((packet[0] & 0x0F) * 4)..];
        }
        if (icmp.Length < 8) return false;
        int type = icmp[0];
        (bool echo, IPStatus kind) = family == AddressFamily.InterNetwork
            ? type switch { 0 => (true, IPStatus.Success), 11 => (false, IPStatus.TtlExpired), 3 => (false, IPStatus.DestinationUnreachable), _ => (false, IPStatus.Unknown) }
            : type switch { 129 => (true, IPStatus.Success), 3 => (false, IPStatus.TtlExpired), 1 => (false, IPStatus.DestinationUnreachable), _ => (false, IPStatus.Unknown) };
        if (kind == IPStatus.Unknown) return false;
        ReadOnlySpan<byte> request;
        if (echo) request = icmp;
        else
        {
            // Error messages quote the original IP header followed by at least 8 bytes of our echo request.
            var inner = icmp[8..];
            if (family == AddressFamily.InterNetwork)
            {
                if (inner.Length < 20 || inner[9] != 1) return false;
                request = inner[((inner[0] & 0x0F) * 4)..];
            }
            else
            {
                if (inner.Length < 40 || inner[6] != 58) return false;
                request = inner[40..];
            }
            if (request.Length < 8 || request[0] != (family == AddressFamily.InterNetwork ? 8 : 128)) return false;
        }
        if (request.Length < 8 || BinaryPrimitives.ReadUInt16BigEndian(request[4..]) != identifier) return false;
        sequence = BinaryPrimitives.ReadUInt16BigEndian(request[6..]);
        status = kind;
        return true;
    }
}
