using System.Diagnostics;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
namespace VmPing.Core;

public record ProbeSettings(int IntervalMs, int TimeoutMs);
public static class NetworkProbe
{
    public static readonly TimeSpan DnsRefresh = TimeSpan.FromMinutes(5);
    public static async Task<ProbeResult> SendAsync(Target target, int timeoutMs, CancellationToken token)
    {
        IPAddress address;
        try { address = await ResolveAsync(target.Host, token); }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
        catch (Exception ex) when (ex is SocketException or ArgumentException)
        { return new(DateTimeOffset.Now, ResultKind.LocalError, null, $"Falha DNS: {ex.Message}"); }
        return await SendAsync(target, address, timeoutMs, token);
    }
    public static async Task<IPAddress> ResolveAsync(string host, CancellationToken token)
    {
        if (IPAddress.TryParse(host, out var literal)) return literal;
        var addresses = await Dns.GetHostAddressesAsync(host, token);
        return addresses.FirstOrDefault() ?? throw new SocketException((int)SocketError.HostNotFound);
    }
    public static async Task<ProbeResult> SendAsync(Target target, IPAddress address, int timeoutMs, CancellationToken token)
    {
        try
        {
            if (target.Port is int port)
            {
                using var client = new TcpClient(address.AddressFamily);
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
                timeout.CancelAfter(timeoutMs);
                var watch = Stopwatch.StartNew();
                try { await client.ConnectAsync(address, port, timeout.Token); }
                catch (OperationCanceledException) when (!token.IsCancellationRequested)
                { return new(DateTimeOffset.Now, ResultKind.Timeout, null, "Tempo esgotado"); }
                return new(DateTimeOffset.Now, ResultKind.Success, watch.Elapsed.TotalMilliseconds, $"{address} · porta aberta");
            }
            using var ping = new Ping();
            var start = Stopwatch.GetTimestamp();
            // Empty payload also supports unprivileged ICMP sockets on Linux.
            var reply = await ping.SendPingAsync(address, TimeSpan.FromMilliseconds(timeoutMs), [], new PingOptions(64, false), token);
            var elapsed = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
            return new(DateTimeOffset.Now, reply.Status == IPStatus.Success ? ResultKind.Success :
                reply.Status == IPStatus.TimedOut ? ResultKind.Timeout : ResultKind.Unreachable,
                reply.Status == IPStatus.Success ? PreciseRoundtrip(reply.RoundtripTime, elapsed) : null, $"{reply.Address} · {reply.Status}");
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
        catch (SocketException ex) when (ex.SocketErrorCode == SocketError.ConnectionRefused)
        { return new(DateTimeOffset.Now, ResultKind.Unreachable, null, $"{address} · conexão recusada"); }
        catch (Exception ex) when (ex is PingException or SocketException or ArgumentException or System.Security.SecurityException)
        { return new(DateTimeOffset.Now, ResultKind.LocalError, null, ex.InnerException?.Message ?? ex.Message); }
    }
    // PingReply.RoundtripTime is truncated to whole milliseconds. The local stopwatch is never shorter than the
    // real RTT, so inside the same millisecond bucket it is the better estimate. Outside it the API overhead
    // (for example the ping process fallback on Linux) dominates and the reported value is kept.
    public static double PreciseRoundtrip(long reported, double elapsed) => elapsed >= reported && elapsed < reported + 1 ? elapsed : reported;
    // Measure start-to-start. Never overlap probes or replay missed intervals after a timeout.
    public static Task RunAsync(Target target, int intervalMs, int timeoutMs, Action<ProbeResult> receive, CancellationToken token) =>
        RunAsync(target, () => new(intervalMs, timeoutMs), receive, token);
    // Settings are read before every probe so changes apply to running hosts. DNS names are resolved once and
    // refreshed periodically; a failed refresh keeps the last known address.
    public static async Task RunAsync(Target target, Func<ProbeSettings> settings, Action<ProbeResult> receive, CancellationToken token)
    {
        IPAddress? address = null;
        var resolvedAt = 0L;
        while (!token.IsCancellationRequested)
        {
            var start = Stopwatch.GetTimestamp();
            var current = settings();
            ProbeResult result;
            try
            {
                if (address is null || Stopwatch.GetElapsedTime(resolvedAt) > DnsRefresh)
                {
                    try { address = await ResolveAsync(target.Host, token); resolvedAt = Stopwatch.GetTimestamp(); }
                    catch (Exception ex) when (address is not null && ex is SocketException or ArgumentException) { resolvedAt = Stopwatch.GetTimestamp(); }
                }
                result = await SendAsync(target, address, current.TimeoutMs, token);
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
            catch (SocketException ex) { result = new(DateTimeOffset.Now, ResultKind.LocalError, null, $"Falha DNS: {ex.Message}"); }
            catch (Exception ex) { result = new(DateTimeOffset.Now, ResultKind.LocalError, null, ex.Message); }
            receive(result);
            var delay = TimeSpan.FromMilliseconds(current.IntervalMs) - Stopwatch.GetElapsedTime(start);
            if (delay > TimeSpan.Zero) await Task.Delay(delay, token);
        }
    }
}
