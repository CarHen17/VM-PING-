using System.Diagnostics;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
namespace VmPing.Core;

public static class NetworkProbe
{
    public static async Task<ProbeResult> SendAsync(Target target, int timeoutMs, CancellationToken token)
    {
        var watch = Stopwatch.StartNew();
        try
        {
            if (target.Port is int port)
            {
                using var client = new TcpClient();
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
                timeout.CancelAfter(timeoutMs);
                try { await client.ConnectAsync(target.Host, port, timeout.Token); }
                catch (OperationCanceledException) when (!token.IsCancellationRequested)
                { return new(DateTimeOffset.Now, ResultKind.Timeout, null, "Tempo esgotado"); }
                return new(DateTimeOffset.Now, ResultKind.Success, watch.Elapsed.TotalMilliseconds, "Porta aberta");
            }
            using var ping = new Ping();
            // Empty payload also supports unprivileged ICMP sockets on Linux.
            var reply = await ping.SendPingAsync(target.Host, TimeSpan.FromMilliseconds(timeoutMs), [], new PingOptions(64, false), token);
            return new(DateTimeOffset.Now, reply.Status == IPStatus.Success ? ResultKind.Success :
                reply.Status == IPStatus.TimedOut ? ResultKind.Timeout : ResultKind.Unreachable,
                reply.Status == IPStatus.Success ? reply.RoundtripTime : null, $"{reply.Address} · {reply.Status}");
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
        catch (SocketException ex) when (ex.SocketErrorCode == SocketError.ConnectionRefused)
        { return new(DateTimeOffset.Now, ResultKind.Unreachable, null, "Conexão recusada"); }
        catch (Exception ex) when (ex is PingException or SocketException or ArgumentException or System.Security.SecurityException)
        { return new(DateTimeOffset.Now, ResultKind.LocalError, null, ex.InnerException?.Message ?? ex.Message); }
    }
    // Measure start-to-start. Never overlap probes or replay missed intervals after a timeout.
    public static async Task RunAsync(Target target, int intervalMs, int timeoutMs,
        Action<ProbeResult> receive, CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            var start = Stopwatch.GetTimestamp();
            receive(await SendAsync(target, timeoutMs, token));
            var delay = TimeSpan.FromMilliseconds(intervalMs) - Stopwatch.GetElapsedTime(start);
            if (delay > TimeSpan.Zero) await Task.Delay(delay, token);
        }
    }
}
