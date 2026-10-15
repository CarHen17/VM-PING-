using System.Net;
namespace VmPing.Core;

public enum ProbeKind { Icmp, Tcp }
public enum ResultKind { Success, Timeout, Unreachable, LocalError }
public record Target(string Host, int? Port = null, string Alias = "", string Group = "")
{
    public ProbeKind Kind => Port.HasValue ? ProbeKind.Tcp : ProbeKind.Icmp;
    public string Address => Port is null ? Host : $"{(Host.Contains(':') ? $"[{Host}]" : Host)}:{Port}";
    public static Target Parse(string text, string alias = "")
    {
        text = text.Trim();
        if (text.Length == 0) throw new FormatException("Informe um endereço.");
        if (text.StartsWith('['))
        {
            var end = text.IndexOf(']');
            if (end < 0) throw new FormatException("IPv6 inválido. Use [endereço]:porta.");
            var host = text[1..end];
            if (!IPAddress.TryParse(host, out _)) throw new FormatException("IPv6 inválido.");
            if (end == text.Length - 1) return new(host, null, alias);
            if (text[end + 1] != ':') throw new FormatException("Use [endereço]:porta.");
            return new(host, ParsePort(text[(end + 2)..]), alias);
        }
        if (IPAddress.TryParse(text, out _)) return new(text, null, alias);
        if (text.Contains(':'))
        {
            var parts = text.Split(':');
            if (parts.Length != 2) throw new FormatException("IPv6 inválido.");
            ValidateHost(parts[0]);
            return new(parts[0], ParsePort(parts[1]), alias);
        }
        ValidateHost(text);
        return new(text, null, alias);
    }
    private static int ParsePort(string value) => int.TryParse(value, out var port) && port is > 0 and <= 65535
        ? port : throw new FormatException("A porta deve estar entre 1 e 65535.");
    private static void ValidateHost(string host)
    {
        if (Uri.CheckHostName(host) == UriHostNameType.Unknown) throw new FormatException($"Host inválido: {host}");
    }
}
public record ProbeResult(DateTimeOffset Time, ResultKind Kind, double? Milliseconds, string Detail);
public sealed class Statistics
{
    public long Sent { get; private set; }
    public long Received { get; private set; }
    public long Lost { get; private set; }
    public long Errors { get; private set; }
    public double Min { get; private set; } = double.PositiveInfinity;
    public double Max { get; private set; }
    private double total, jitterTotal;
    private long jitterSamples;
    private double? previous;
    public double Average => Received == 0 ? 0 : total / Received;
    // Mean absolute difference between consecutive successful replies.
    public double Jitter => jitterSamples == 0 ? 0 : jitterTotal / jitterSamples;
    public bool HasJitter => jitterSamples > 0;
    public double LossPercent => Sent - Errors == 0 ? 0 : 100d * Lost / (Sent - Errors);
    public void Add(ProbeResult result)
    {
        Sent++;
        if (result.Kind == ResultKind.LocalError) { Errors++; return; }
        if (result.Kind != ResultKind.Success) { Lost++; return; }
        Received++;
        var time = result.Milliseconds ?? 0;
        Min = Math.Min(Min, time); Max = Math.Max(Max, time); total += time;
        if (previous is double last) { jitterTotal += Math.Abs(time - last); jitterSamples++; }
        previous = time;
    }
}
// Degraded is not produced by StatusTracker; it is layered on top of Online by DegradationMonitor.
public enum HostStatus { Stopped, Online, Down, LocalError, Degraded }
// Flags a host that answers but is too slow or loses too many packets over a sliding window.
public sealed class DegradationMonitor
{
    public double LatencyMs { get; set; }
    public double LossPercent { get; set; }
    public int Window { get; set; } = 20;
    public int MinimumSamples { get; set; } = 5;
    private readonly Queue<double?> samples = new();
    public bool IsDegraded { get; private set; }
    public string Reason { get; private set; } = "";
    public double RecentAverage { get; private set; }
    public double RecentLoss { get; private set; }
    // Local errors say nothing about the remote link and are ignored.
    public bool Add(ResultKind kind, double? milliseconds)
    {
        if (kind == ResultKind.LocalError) return Evaluate();
        samples.Enqueue(kind == ResultKind.Success ? milliseconds ?? 0 : null);
        while (samples.Count > Math.Max(1, Window)) samples.Dequeue();
        return Evaluate();
    }
    public bool Evaluate()
    {
        var replies = samples.Where(x => x is not null).Select(x => x!.Value).ToList();
        RecentLoss = samples.Count == 0 ? 0 : 100d * (samples.Count - replies.Count) / samples.Count;
        RecentAverage = replies.Count == 0 ? 0 : replies.Average();
        var reasons = new List<string>();
        if (samples.Count >= MinimumSamples)
        {
            if (LatencyMs > 0 && replies.Count > 0 && RecentAverage > LatencyMs) reasons.Add($"latência média {RecentAverage:F0} ms > {LatencyMs:F0} ms");
            if (LossPercent > 0 && RecentLoss > LossPercent) reasons.Add($"perda {RecentLoss:F0}% > {LossPercent:F0}%");
        }
        var previous = IsDegraded;
        IsDegraded = reasons.Count > 0;
        Reason = string.Join(" · ", reasons);
        return IsDegraded != previous;
    }
    public void Reset() { samples.Clear(); IsDegraded = false; Reason = ""; RecentAverage = RecentLoss = 0; }
}
// Debounces status changes so a single lost packet does not flap the host state.
public sealed class StatusTracker(int failuresToDown = 3, int successesToUp = 1)
{
    public int FailuresToDown { get; set; } = Math.Max(1, failuresToDown);
    public int SuccessesToUp { get; set; } = Math.Max(1, successesToUp);
    public HostStatus Status { get; private set; } = HostStatus.Stopped;
    public int ConsecutiveFailures { get; private set; }
    public int ConsecutiveSuccesses { get; private set; }
    // Returns true when Status changed.
    public bool Add(ResultKind kind)
    {
        var previous = Status;
        if (kind == ResultKind.Success)
        {
            ConsecutiveSuccesses++; ConsecutiveFailures = 0;
            if (Status == HostStatus.Stopped || ConsecutiveSuccesses >= SuccessesToUp) Status = HostStatus.Online;
        }
        else
        {
            ConsecutiveFailures++; ConsecutiveSuccesses = 0;
            var failed = kind == ResultKind.LocalError ? HostStatus.LocalError : HostStatus.Down;
            if (Status == HostStatus.Stopped || ConsecutiveFailures >= FailuresToDown) Status = failed;
        }
        return Status != previous;
    }
    public void Stop() { Status = HostStatus.Stopped; ConsecutiveFailures = ConsecutiveSuccesses = 0; }
}
public record Favorite(string Name, List<Target> Targets);
public sealed class Workspace
{
    public List<Target> Targets { get; set; } = [];
    public List<Favorite> Favorites { get; set; } = [];
    public Dictionary<string, string> Aliases { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public int IntervalMs { get; set; } = 1000;
    public int TimeoutMs { get; set; } = 1000;
    public bool DarkTheme { get; set; } = true;
    public int FailuresToDown { get; set; } = 3;
    public int SuccessesToUp { get; set; } = 1;
    public double DegradedLatencyMs { get; set; }
    public double DegradedLossPercent { get; set; }
    public int SampleRetentionDays { get; set; } = 7;
    public bool ListView { get; set; }
    public int SortMode { get; set; }
    public List<string> CollapsedGroups { get; set; } = [];
    public AlertSettings Alerts { get; set; } = new();
}
public sealed class AlertSettings
{
    public bool TelegramEnabled { get; set; }
    public string TelegramToken { get; set; } = "";
    public string TelegramChatId { get; set; } = "";
}
