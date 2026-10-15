using System.Net;
namespace VmPing.Core;

public enum ProbeKind { Icmp, Tcp }
public enum ResultKind { Success, Timeout, Unreachable, LocalError }
public record Target(string Host, int? Port = null, string Alias = "")
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
    private double total;
    public double Average => Received == 0 ? 0 : total / Received;
    public double LossPercent => Sent - Errors == 0 ? 0 : 100d * Lost / (Sent - Errors);
    public void Add(ProbeResult result)
    {
        Sent++;
        if (result.Kind == ResultKind.LocalError) { Errors++; return; }
        if (result.Kind != ResultKind.Success) { Lost++; return; }
        Received++;
        var time = result.Milliseconds ?? 0;
        Min = Math.Min(Min, time); Max = Math.Max(Max, time); total += time;
    }
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
}
