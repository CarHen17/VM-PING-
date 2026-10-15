using System.ComponentModel;
using System.Runtime.CompilerServices;
using Avalonia;
using Avalonia.Styling;
using Avalonia.Media;
using VmPing.Core;
namespace VmPing.Desktop;

public abstract class Observable : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;
    protected void Changed([CallerMemberName] string? name = null) => PropertyChanged?.Invoke(this, new(name));
    protected void Refresh() => Changed("");
}
public sealed class Card(Target target) : Observable
{
    public Target Target { get; } = target;
    public string Title => string.IsNullOrWhiteSpace(Target.Alias) ? Target.Host : Target.Alias;
    public string Address => $"{Target.Kind.ToString().ToUpperInvariant()} · {Target.Address}";
    public Statistics Statistics { get; } = new();
    public StatusTracker Tracker { get; } = new();
    public HostStatus Status => Tracker.Status;
    public event Action<Card>? StatusChanged;
    public IBrush Accent => StatusColors.For(Status);
    public string StatusLabel => Label(Status);
    public static string Label(HostStatus status) => status switch
    {
        HostStatus.Online => "Online", HostStatus.Down => "Sem resposta", HostStatus.LocalError => "Erro local", _ => "Parado"
    };
    public IBrush BadgeBackground => new SolidColorBrush(((ISolidColorBrush)Accent).Color, 0.12);
    public string Loss => $"{Statistics.LossPercent:F1}%";
    public string Minimum => Statistics.Received == 0 ? "—" : $"{Statistics.Min:F1} ms";
    public string Average => Statistics.Received == 0 ? "—" : $"{Statistics.Average:F1} ms";
    public string Maximum => Statistics.Received == 0 ? "—" : $"{Statistics.Max:F1} ms";
    public string Jitter => Statistics.HasJitter ? $"{Statistics.Jitter:F1} ms" : "—";
    public string PacketSummary => $"{Statistics.Sent} env. · {Statistics.Errors} erros";
    public string ActionLabel => Cancellation is null ? "Iniciar" : "Parar";
    public void RefreshPresentation() => Refresh();
    public string Latency { get; private set; } = "— ms";
    public string Detail { get; private set; } = "Aguardando início";
    public double?[] Samples { get; private set; } = [];
    public CancellationTokenSource? Cancellation { get; set; }
    public Task? Running { get; set; }
    // Returns true when the debounced status changed.
    public bool SetResult(ProbeResult result)
    {
        Statistics.Add(result);
        var changed = Tracker.Add(result.Kind);
        Latency = result.Milliseconds is double milliseconds ? $"{milliseconds:F1} ms" : "— ms";
        var streak = result.Kind != ResultKind.Success && Status == HostStatus.Online ? $" · falha {Tracker.ConsecutiveFailures}/{Tracker.FailuresToDown}" : "";
        Detail = $"{result.Time:HH:mm:ss} · {result.Detail}{streak}";
        var samples = new double?[Math.Min(Samples.Length + 1, 60)];
        Array.Copy(Samples, Samples.Length - (samples.Length - 1), samples, 0, samples.Length - 1);
        samples[^1] = result.Milliseconds;
        Samples = samples;
        Refresh();
        if (changed) StatusChanged?.Invoke(this);
        return changed;
    }
    public void Stopped() { Tracker.Stop(); Refresh(); StatusChanged?.Invoke(this); }
}
public sealed class Hop(int ttl) : Observable
{
    public int Ttl { get; } = ttl;
    public string Address { get; private set; } = "*";
    public string DnsName { get; private set; } = "";
    public long Sent { get; private set; }
    public string ResponseStatus { get; private set; } = "Aguardando";
    // Every router seen at this TTL. Load-balanced (ECMP) paths answer from different addresses.
    private readonly Dictionary<string, (string Name, long Count)> paths = [];
    public int PathCount => paths.Count;
    public string Paths => string.Join("\n", paths.OrderByDescending(x => x.Value.Count)
        .Select(x => $"{x.Key}{(string.IsNullOrEmpty(x.Value.Name) ? "" : $" ({x.Value.Name})")} · {x.Value.Count} respostas"));
    public string Subtitle => (!string.IsNullOrEmpty(DnsName) ? DnsName : ResponseStatus switch
    {
        "Success" => "Destino respondeu · sem registro PTR",
        "TtlExpired" or "TimeExceeded" => "Salto respondeu · sem registro PTR",
        "TimedOut" => "Sem resposta",
        "Aguardando" => "Aguardando resposta",
        _ => "Destino inacessível"
    }) + (paths.Count > 1 ? $" · ECMP: {paths.Count} roteadores" : "");
    public IBrush LossBrush => StatusColors.For(Sent == received ? HostStatus.Online : HostStatus.Down);
    public void RefreshPresentation() => Refresh();
    private long received;
    private double total, minimum = double.PositiveInfinity, maximum;
    public string Loss => $"{(Sent == 0 ? 0 : 100d * (Sent - received) / Sent):F1}%";
    public string Last { get; private set; } = "—";
    public string Average => received == 0 ? "—" : $"{total / received:F1}";
    public string Min => received == 0 ? "—" : $"{minimum:F1}";
    public string Max => received == 0 ? "—" : $"{maximum:F1}";
    public void Add(HopResult result)
    {
        Sent++;
        ResponseStatus = result.Status.ToString();
        if (result.Address is not null)
        {
            Address = result.Address.ToString(); DnsName = result.DnsName;
            var count = paths.TryGetValue(Address, out var known) ? known.Count : 0;
            paths[Address] = (result.DnsName, count + 1);
        }
        Last = "—";
        if (result.Milliseconds is double time)
        {
            received++; total += time; minimum = Math.Min(minimum, time); maximum = Math.Max(maximum, time); Last = $"{time:F1}";
        }
        Refresh();
    }
}

internal static class StatusColors
{
    private static readonly IBrush[] Dark = [Brush.Parse("#929DB2"), Brush.Parse("#47D7AC"), Brush.Parse("#FF8A97"), Brush.Parse("#EDBD75")];
    private static readonly IBrush[] Light = [Brush.Parse("#64718A"), Brush.Parse("#087D60"), Brush.Parse("#BC294A"), Brush.Parse("#99600B")];
    public static IBrush For(HostStatus status) =>
        (Application.Current?.ActualThemeVariant == ThemeVariant.Light ? Light : Dark)[(int)status];
}
