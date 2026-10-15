using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.LogicalTree;
using Avalonia.Threading;
using VmPing.Core;
namespace VmPing.Desktop;

public partial class HostHistoryWindow : Window
{
    private readonly SampleStore? store;
    private readonly Target target = new("localhost");
    private TimeSpan range = TimeSpan.FromHours(1);
    private const string Hint = "Passe o mouse no gráfico para ver cada intervalo. Linha: média · faixa: mín–máx · barras vermelhas: perda.";
    public HostHistoryWindow() => InitializeComponent();
    public HostHistoryWindow(SampleStore store, Target target, string title) : this()
    {
        this.store = store; this.target = target;
        HostTitle.Text = title; HostAddress.Text = $"{target.Kind.ToString().ToUpperInvariant()} · {target.Address}";
        Title = $"Histórico · {title}";
        Chart.Hovered += bucket => HoverText.Text = bucket is null ? Hint :
            $"{bucket.Start.ToLocalTime():dd/MM HH:mm:ss} – {bucket.End.ToLocalTime():HH:mm:ss} · média {Format(bucket.Average)} · mín {Format(bucket.Minimum)} · máx {Format(bucket.Maximum)} · perda {bucket.LossPercent:F1}% · {bucket.Sent} sondagens{(bucket.Errors > 0 ? $" · {bucket.Errors} erros locais" : "")}";
        Opened += async (_, _) => await LoadAsync();
    }
    private static string Format(double? value) => value is double v ? $"{v:F1} ms" : "—";
    private async void RangeClick(object? sender, RoutedEventArgs e)
    {
        if ((sender as Button)?.Tag is string hours) range = TimeSpan.FromHours(int.Parse(hours));
        await LoadAsync();
    }
    private async void RefreshClick(object? sender, RoutedEventArgs e) => await LoadAsync();
    private async Task LoadAsync()
    {
        if (store is null) return;
        foreach (var button in this.GetLogicalDescendants().OfType<Button>().Where(x => x.Classes.Contains("range")))
            button.Classes.Set("active", button.Tag as string == ((int)range.TotalHours).ToString());
        var to = DateTimeOffset.Now; var from = to - range;
        var width = Math.Max(200, (int)Chart.Bounds.Width - 60);
        List<SampleBucket> buckets;
        try { buckets = await Task.Run(() => store.Load(target, from, to, Math.Min(width / 2, 600))); }
        catch (Exception ex) { HoverText.Text = $"Não foi possível ler o histórico: {ex.Message}"; return; }
        Chart.From = from; Chart.To = to; Chart.Buckets = buckets;
        EmptyMessage.IsVisible = buckets.Count == 0;
        long sent = buckets.Sum(x => x.Sent), errors = buckets.Sum(x => x.Errors), received = buckets.Sum(x => x.Received);
        var valid = sent - errors;
        Availability.Text = valid == 0 ? "—" : $"{100d * received / valid:F2}%";
        LossValue.Text = valid == 0 ? "—" : $"{100d * (valid - received) / valid:F2}%";
        AverageValue.Text = received == 0 ? "—" : $"{buckets.Where(x => x.Average is not null).Sum(x => x.Average!.Value * x.Received) / received:F1} ms";
        MaximumValue.Text = Format(buckets.Max(x => x.Maximum));
        SentValue.Text = sent.ToString("N0");
    }
}
