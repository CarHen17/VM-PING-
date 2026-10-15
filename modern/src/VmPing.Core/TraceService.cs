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
    public static async Task RunAsync(string host, AddressFamily family, bool continuous, int maxHops,
        int timeoutMs, int intervalMs, Action<HopResult> receive, Action<IPAddress> resolved, CancellationToken token)
    {
        var destination = await ResolveAsync(host, family, token);
        resolved(destination);
        var names = new Dictionary<IPAddress, string>();
        using var ping = new Ping();
        do
        {
            var cycle = Stopwatch.GetTimestamp();
            for (var ttl = 1; ttl <= maxHops; ttl++)
            {
                token.ThrowIfCancellationRequested();
                var start = Stopwatch.GetTimestamp();
                var reply = await ping.SendPingAsync(destination, TimeSpan.FromMilliseconds(timeoutMs), [], new PingOptions(ttl, false), token);
                // PingReply.RoundtripTime is only valid for Success. Measure TTL-exceeded responses ourselves.
                var elapsed = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
                var address = reply.Address;
                if (address is not null && (address.Equals(IPAddress.Any) || address.Equals(IPAddress.IPv6Any))) address = null;
                var responded = reply.Status is IPStatus.Success or IPStatus.TtlExpired or IPStatus.TimeExceeded;
                var name = "";
                if (address is not null && responded)
                {
                    if (!names.TryGetValue(address, out name))
                    {
                        try
                        {
                            using var dnsTimeout = CancellationTokenSource.CreateLinkedTokenSource(token);
                            dnsTimeout.CancelAfter(700);
                            name = (await Dns.GetHostEntryAsync(address).WaitAsync(dnsTimeout.Token)).HostName;
                        }
                        catch (Exception ex) when (ex is SocketException or OperationCanceledException or ArgumentException)
                        { token.ThrowIfCancellationRequested(); name = ""; }
                        names[address] = name;
                    }
                }
                double? milliseconds = reply.Status == IPStatus.Success ? reply.RoundtripTime : responded ? elapsed : null;
                receive(new(ttl, address, name ?? "", milliseconds, reply.Status));
                if (reply.Status == IPStatus.Success && address?.Equals(destination) == true) break;
            }
            if (!continuous) break;
            var remaining = TimeSpan.FromMilliseconds(intervalMs) - Stopwatch.GetElapsedTime(cycle);
            if (remaining > TimeSpan.Zero) await Task.Delay(remaining, token);
        } while (!token.IsCancellationRequested);
    }
}
