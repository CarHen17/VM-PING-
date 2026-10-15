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
    public string Status { get; private set; } = "PARADO";
    public IBrush Accent => StatusColors.For(Status);
    public string StatusLabel => Status switch { "ONLINE" => "Online", "SEM RESPOSTA" => "Sem resposta", "ERRO LOCAL" => "Erro local", _ => "Parado" };
    public IBrush BadgeBackground => new SolidColorBrush(((ISolidColorBrush)Accent).Color, 0.12);
    public string Loss => $"{Statistics.LossPercent:F1}%";
    public string Minimum => Statistics.Received == 0 ? "—" : $"{Statistics.Min:F1} ms";
    public string Average => Statistics.Received == 0 ? "—" : $"{Statistics.Average:F1} ms";
    public string Maximum => Statistics.Received == 0 ? "—" : $"{Statistics.Max:F1} ms";
    public string PacketSummary => $"{Statistics.Sent} env. · {Statistics.Errors} erros";
    public string ActionLabel => Cancellation is null ? "Iniciar" : "Parar";
    public void RefreshPresentation() => Refresh();
    public string Latency { get; private set; } = "— ms";
    public string Detail { get; private set; } = "Aguardando início";
    public double?[] Samples { get; private set; } = [];
    public string Summary => $"Enviados {Statistics.Sent} · perda {Statistics.LossPercent:F1}% · erros {Statistics.Errors}\nMín {(Statistics.Received > 0 ? Statistics.Min : 0):F1} · méd {Statistics.Average:F1} · máx {Statistics.Max:F1} ms";
    public CancellationTokenSource? Cancellation { get; set; }
    public Task? Running { get; set; }
    public void SetResult(ProbeResult result)
    {
        Statistics.Add(result);
        Status = result.Kind == ResultKind.Success ? "ONLINE" : result.Kind == ResultKind.LocalError ? "ERRO LOCAL" : "SEM RESPOSTA";
        Latency = result.Milliseconds is double milliseconds ? $"{milliseconds:F1} ms" : "— ms";
        Detail = $"{result.Time:HH:mm:ss} · {result.Detail}";
        Samples = Samples.TakeLast(59).Append(result.Milliseconds).ToArray();
        Refresh();
    }
    public void Stopped() { Status = "PARADO"; Refresh(); }
}
public sealed class Hop(int ttl) : Observable
{
    public int Ttl { get; } = ttl;
    public string Address { get; private set; } = "*";
    public string DnsName { get; private set; } = "";
    public long Sent { get; private set; }
    public string ResponseStatus { get; private set; } = "Aguardando";
    public string Subtitle => !string.IsNullOrEmpty(DnsName) ? DnsName : ResponseStatus switch
    {
        "Success" => "Destino respondeu · sem registro PTR",
        "TtlExpired" or "TimeExceeded" => "Salto respondeu · sem registro PTR",
        "TimedOut" => "Sem resposta",
        "Aguardando" => "Aguardando resposta",
        _ => "Destino inacessível"
    };
    public IBrush LossBrush => StatusColors.For(Sent == received ? "ONLINE" : "SEM RESPOSTA");
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
        if (result.Address is not null && Address != "*" && Address != result.Address.ToString())
        {
            Sent = received = 0; total = maximum = 0; minimum = double.PositiveInfinity;
        }
        Sent++;
        ResponseStatus = result.Status.ToString();
        if (result.Address is not null)
        {
            Address = result.Address.ToString(); DnsName = result.DnsName;
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
    public static IBrush For(string status)
    {
        var light = Application.Current?.ActualThemeVariant == ThemeVariant.Light;
        return Brush.Parse(status switch
        {
            "ONLINE" => light ? "#087D60" : "#47D7AC",
            "SEM RESPOSTA" => light ? "#BC294A" : "#FF8A97",
            "ERRO LOCAL" => light ? "#99600B" : "#EDBD75",
            _ => light ? "#64718A" : "#929DB2"
        });
    }
}
