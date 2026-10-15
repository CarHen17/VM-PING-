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
    // TTLs are probed one at a time: on Linux, concurrent System.Net.Ping requests receive each other's
    // TTL-exceeded replies (the embedded original packet is not matched), which assigns routers to wrong hops.
    public static async Task RunAsync(string host, AddressFamily family, bool continuous, int maxHops,
        int timeoutMs, int intervalMs, Action<HopResult> receive, Action<IPAddress> resolved, CancellationToken token)
    {
        var destination = await ResolveAsync(host, family, token);
        resolved(destination);
        var names = new Dictionary<IPAddress, string>();
        do
        {
            var cycle = Stopwatch.GetTimestamp();
            for (var ttl = 1; ttl <= maxHops; ttl++)
            {
                token.ThrowIfCancellationRequested();
                var reply = await ProbeAsync(destination, ttl, timeoutMs, token);
                if (reply.Address is not null && reply.Milliseconds is not null && !names.ContainsKey(reply.Address))
                    await ResolveNamesAsync([reply.Address], names, token);
                receive(reply with { DnsName = reply.Address is null ? "" : names.GetValueOrDefault(reply.Address, "") });
                if (reply.Status == IPStatus.Success && reply.Address?.Equals(destination) == true) break;
            }
            if (!continuous) break;
            var remaining = TimeSpan.FromMilliseconds(intervalMs) - Stopwatch.GetElapsedTime(cycle);
            if (remaining > TimeSpan.Zero) await Task.Delay(remaining, token);
        } while (!token.IsCancellationRequested);
    }
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
