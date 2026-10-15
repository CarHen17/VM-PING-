using System.Diagnostics;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
namespace VmPing.Core;

public record HopResult(int Ttl, IPAddress? Address, string DnsName, double? Milliseconds, IPStatus Status);
public static class TraceService
{
    public static async Task<IPAddress> ResolveAsync(string host, AddressFamily family, CancellationToken token)
    {
        var addresses = await Dns.GetHostAddressesAsync(host, token);
        return addresses.FirstOrDefault(x => x.AddressFamily == family)
            ?? throw new InvalidOperationException(family == AddressFamily.InterNetwork ? "Destino sem endereço IPv4." : "Destino sem endereço IPv6.");
    }
    public enum Mode { Sequential, ParallelSystem, ParallelRaw }
    public static Mode DefaultMode(AddressFamily family)
    {
        // Windows IcmpSendEcho2 matches each reply to its own request (WinMTR relies on this), so concurrent Ping
        // calls are safe there. On Linux, concurrent System.Net.Ping requests receive each other's TTL-exceeded
        // replies, so parallel probing needs our own raw socket, which requires root or CAP_NET_RAW.
        if (OperatingSystem.IsWindows()) return Mode.ParallelSystem;
        using var raw = RawIcmpTracer.TryCreate(family);
        return raw is null ? Mode.Sequential : Mode.ParallelRaw;
    }
    public static string Describe(Mode mode) => mode switch
    {
        Mode.ParallelSystem => "saltos em paralelo",
        Mode.ParallelRaw => "saltos em paralelo (socket ICMP próprio)",
        _ => "saltos em sequência (sem permissão de socket ICMP)"
    };
    public static Task RunAsync(string host, AddressFamily family, bool continuous, int maxHops,
        int timeoutMs, int intervalMs, Action<HopResult> receive, Action<IPAddress> resolved, CancellationToken token) =>
        RunAsync(host, family, continuous, maxHops, timeoutMs, intervalMs, receive, resolved, DefaultMode(family), token);
    public static async Task RunAsync(string host, AddressFamily family, bool continuous, int maxHops,
        int timeoutMs, int intervalMs, Action<HopResult> receive, Action<IPAddress> resolved, Mode mode, CancellationToken token)
    {
        var destination = await ResolveAsync(host, family, token);
        resolved(destination);
        var names = new Dictionary<IPAddress, string>();
        using var raw = mode == Mode.ParallelRaw ? RawIcmpTracer.TryCreate(family) : null;
        if (mode == Mode.ParallelRaw && raw is null) mode = Mode.Sequential;
        // Shortest TTL at which the destination answered. Lost replies on lower TTLs can make it answer "later" in a
        // single cycle; that must not move the destination row. A route that really got longer is accepted after a
        // few cycles in which the destination never answers at the known TTL.
        int? destinationTtl = null;
        var missedAtKnown = 0;
        do
        {
            var cycle = Stopwatch.GetTimestamp();
            if (mode == Mode.Sequential)
            {
                for (var ttl = 1; ttl <= maxHops; ttl++)
                {
                    token.ThrowIfCancellationRequested();
                    var reply = await ProbeAsync(destination, ttl, timeoutMs, token);
                    if (reply.Address is not null && reply.Milliseconds is not null && !names.ContainsKey(reply.Address))
                        await ResolveNamesAsync([reply.Address], names, token);
                    receive(reply with { DnsName = reply.Address is null ? "" : names.GetValueOrDefault(reply.Address, "") });
                    if (IsDestination(reply, destination)) break;
                }
            }
            else
            {
                // Once the path length is known only probe up to it (plus slack for a longer path); if the
                // destination stops answering within the limit, the next cycle searches up to maxHops again.
                var limit = Math.Min(maxHops, destinationTtl is int known ? known + 2 : maxHops);
                var replies = raw is not null
                    ? await raw.ProbeAsync(destination, limit, timeoutMs, token)
                    : await Task.WhenAll(Enumerable.Range(1, limit).Select(async ttl =>
                    {
                        await Task.Delay(TimeSpan.FromMilliseconds(20 * (ttl - 1)), token);
                        return await ProbeAsync(destination, ttl, timeoutMs, token);
                    }));
                var reached = Array.FindIndex(replies, x => IsDestination(x, destination));
                if (reached >= 0)
                {
                    var ttl = reached + 1;
                    if (destinationTtl is not int shortest || ttl <= shortest) { destinationTtl = ttl; missedAtKnown = 0; }
                    else if (++missedAtKnown >= 5) { destinationTtl = ttl; missedAtKnown = 0; }
                }
                var reported = replies.Take(destinationTtl ?? replies.Length).ToArray();
                await ResolveNamesAsync(reported.Where(x => x.Address is not null && x.Milliseconds is not null).Select(x => x.Address!)
                    .Distinct().Where(x => !names.ContainsKey(x)).ToList(), names, token);
                foreach (var reply in reported)
                    receive(reply with { DnsName = reply.Address is null ? "" : names.GetValueOrDefault(reply.Address, "") });
            }
            if (!continuous) break;
            var remaining = TimeSpan.FromMilliseconds(intervalMs) - Stopwatch.GetElapsedTime(cycle);
            if (remaining > TimeSpan.Zero) await Task.Delay(remaining, token);
        } while (!token.IsCancellationRequested);
    }
    private static bool IsDestination(HopResult reply, IPAddress destination) =>
        reply.Status == IPStatus.Success && reply.Address?.Equals(destination) == true;
    private static async Task<HopResult> ProbeAsync(IPAddress destination, int ttl, int timeoutMs, CancellationToken token)
    {
        using var ping = new Ping();
        var start = Stopwatch.GetTimestamp();
        var reply = await ping.SendPingAsync(destination, TimeSpan.FromMilliseconds(timeoutMs), [], new PingOptions(ttl, false), token);
        // PingReply.RoundtripTime is only valid for Success. Measure TTL-exceeded responses ourselves.
        var elapsed = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
        var address = reply.Address;
        if (address is not null && (address.Equals(IPAddress.Any) || address.Equals(IPAddress.IPv6Any))) address = null;
        var responded = reply.Status is IPStatus.Success or IPStatus.TtlExpired or IPStatus.TimeExceeded;
        double? milliseconds = reply.Status == IPStatus.Success ? NetworkProbe.PreciseRoundtrip(reply.RoundtripTime, elapsed) : responded ? elapsed : null;
        return new(ttl, reply.Status == IPStatus.TimedOut ? null : address, "", milliseconds, reply.Status);
    }
    private static async Task ResolveNamesAsync(IEnumerable<IPAddress> addresses, Dictionary<IPAddress, string> names, CancellationToken token)
    {
        var lookups = addresses.Select(async address =>
        {
            try
            {
                using var dnsTimeout = CancellationTokenSource.CreateLinkedTokenSource(token);
                dnsTimeout.CancelAfter(700);
                return (address, (await Dns.GetHostEntryAsync(address).WaitAsync(dnsTimeout.Token)).HostName);
            }
            catch (Exception ex) when (ex is SocketException or OperationCanceledException or ArgumentException)
            { token.ThrowIfCancellationRequested(); return (address, ""); }
        }).ToList();
        foreach (var (address, name) in await Task.WhenAll(lookups)) names[address] = name;
    }
}
