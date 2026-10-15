using System.Collections.ObjectModel;
using System.Globalization;
using System.Net.Sockets;
using System.Text;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Avalonia.Styling;
using Avalonia.Threading;
using VmPing.Core;
namespace VmPing.Desktop;

public partial class MainWindow : Window
{
    public static readonly StyledProperty<int> CardColumnsProperty = AvaloniaProperty.Register<MainWindow, int>(nameof(CardColumns), 3);
    public int CardColumns { get => GetValue(CardColumnsProperty); set => SetValue(CardColumnsProperty, value); }
    public ObservableCollection<Card> Cards { get; } = [];
    public ObservableCollection<Hop> Hops { get; } = [];
    public ObservableCollection<string> Events { get; } = [];
    private Workspace workspace = new();
    private readonly string workspacePath;
    private CancellationTokenSource? traceCancellation;
    private Task? traceTask;
    private bool loaded, closing, allowClose;
    public MainWindow() : this(WorkspaceStore.DefaultPath) { }
    public MainWindow(string path)
    {
        workspacePath = path;
        InitializeComponent(); DataContext = this;
        SizeChanged += (_, _) => CardColumns = Math.Clamp((int)((Bounds.Width - 274) / 335), 1, 4);
        Cards.CollectionChanged += (_, e) =>
        {
            if (e.OldItems is not null) foreach (Card card in e.OldItems) card.PropertyChanged -= CardPropertyChanged;
            if (e.NewItems is not null) foreach (Card card in e.NewItems) card.PropertyChanged += CardPropertyChanged;
            UpdateOverview();
        };
        Hops.CollectionChanged += (_, _) => TraceEmpty.IsVisible = Hops.Count == 0;
        Events.CollectionChanged += (_, _) => HistoryEmpty.IsVisible = Events.Count == 0;
        try { workspace = WorkspaceStore.Load(workspacePath); loaded = true; }
        catch (Exception ex) { Footer.Text = $"Não foi possível abrir a configuração: {ex.Message} Salvamento desativado para preservar o arquivo."; }
        IntervalInput.Value = workspace.IntervalMs; TimeoutInput.Value = workspace.TimeoutMs;
        ApplyTheme();
        foreach (var target in workspace.Targets) Cards.Add(new(target));
        UpdateFavorites();
        UpdateOverview();
        Closing += async (_, e) =>
        {
            if (allowClose) return;
            e.Cancel = true;
            if (closing) return;
            closing = true;
            traceCancellation?.Cancel();
            foreach (var card in Cards) card.Cancellation?.Cancel();
            await Task.WhenAll(Cards.Select(x => x.Running ?? Task.CompletedTask).Append(traceTask ?? Task.CompletedTask));
            Save(); allowClose = true; Close();
        };
    }
    private void CardPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e) => UpdateOverview();
    private void UpdateOverview()
    {
        TotalCount.Text = Cards.Count.ToString();
        OnlineCount.Text = Cards.Count(x => x.Status == "ONLINE").ToString();
        DownCount.Text = Cards.Count(x => x.Status == "SEM RESPOSTA").ToString();
        ErrorCount.Text = Cards.Count(x => x.Status == "ERRO LOCAL").ToString();
        HostCountLabel.Text = $"{Cards.Count} configurados";
        MonitorEmpty.IsVisible = Cards.Count == 0;
    }
    private void NavigateClick(object? sender, RoutedEventArgs e)
    {
        var page = (sender as Button)?.Tag as string ?? "Monitor";
        MonitorPage.IsVisible = page == "Monitor";
        TracePage.IsVisible = page == "Trace";
        HistoryPage.IsVisible = page == "History";
        SettingsPage.IsVisible = page == "Settings";
        NavMonitor.Classes.Set("selected", page == "Monitor");
        NavTrace.Classes.Set("selected", page == "Trace");
        NavHistory.Classes.Set("selected", page == "History");
        NavSettings.Classes.Set("selected", page == "Settings");
        (PageTitle.Text, PageDescription.Text) = page switch
        {
            "Trace" => ("Rotas & MTR", "Entenda cada salto entre você e o destino."),
            "History" => ("Histórico", "Acompanhe as mudanças de disponibilidade da sua rede."),
            "Settings" => ("Configurações", "Ajuste suas sondagens e organize os seus favoritos."),
            _ => ("Monitoramento", "Disponibilidade e latência dos seus hosts, em tempo real.")
        };
    }
    private int Interval => (int)(IntervalInput.Value ?? 1000);
    private int Timeout => (int)(TimeoutInput.Value ?? 1000);
    private void AddClick(object? sender, RoutedEventArgs e) => AddTargets();
    private void HostKeyDown(object? sender, Avalonia.Input.KeyEventArgs e) { if (e.Key == Avalonia.Input.Key.Enter) AddTargets(); }
    private void AddTargets()
    {
        try
        {
            var targets = (HostInput.Text ?? "").Split([',', ';', '\r', '\n'], StringSplitOptions.RemoveEmptyEntries)
                .Select(x => Target.Parse(x, AliasInput.Text?.Trim() ?? "")).ToList();
            if (targets.Count == 0) throw new FormatException("Informe pelo menos um host.");
            foreach (var original in targets)
            {
                var target = string.IsNullOrWhiteSpace(original.Alias) ? original with { Alias = workspace.Aliases.GetValueOrDefault(original.Address, "") } : original;
                if (!Cards.Any(x => string.Equals(x.Target.Address, target.Address, StringComparison.OrdinalIgnoreCase))) Cards.Add(new(target));
            }
            HostInput.Text = ""; AliasInput.Text = ""; Save(); Footer.Text = $"{Cards.Count} hosts configurados.";
        }
        catch (Exception ex) { Footer.Text = ex.Message; }
    }
    private void Start(Card card)
    {
        if (closing || card.Running is { IsCompleted: false }) return;
        var cancellation = new CancellationTokenSource(); card.Cancellation = cancellation;
        var interval = Interval; var timeout = Timeout;
        card.Running = RunCard(card, interval, timeout, cancellation);
        card.RefreshPresentation();
    }
    private async Task RunCard(Card card, int interval, int timeout, CancellationTokenSource cancellation)
    {
        try
        {
            await NetworkProbe.RunAsync(card.Target, interval, timeout, result =>
            {
                var previous = card.Status; card.SetResult(result);
                if (previous != card.Status)
                {
                    AddEvent($"{card.Title} ({card.Target.Address}): {card.Status} · {result.Detail}");
                    Footer.Text = $"{result.Time:HH:mm:ss} · {card.Title}: {card.Status} · {result.Detail}";
                }
            }, cancellation.Token);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { AddEvent($"{card.Title}: erro local · {ex.Message}"); Footer.Text = ex.Message; }
        finally { card.Cancellation = null; card.Stopped(); cancellation.Dispose(); }
    }
    private void StartAllClick(object? sender, RoutedEventArgs e) { foreach (var card in Cards) Start(card); Save(); }
    private void StopAllClick(object? sender, RoutedEventArgs e) { foreach (var card in Cards) card.Cancellation?.Cancel(); }
    private void ToggleCardClick(object? sender, RoutedEventArgs e)
    {
        if ((sender as Control)?.DataContext is not Card card) return;
        if (card.Running is { IsCompleted: false }) card.Cancellation?.Cancel(); else Start(card);
    }
    private async void RemoveCardClick(object? sender, RoutedEventArgs e)
    {
        if ((sender as Control)?.DataContext is not Card card) return;
        card.Cancellation?.Cancel(); if (card.Running is not null) await card.Running;
        Cards.Remove(card); Save();
    }
    private void AddEvent(string value)
    {
        Events.Insert(0, $"{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss zzz} · {value}");
        if (Events.Count > 2000) Events.RemoveAt(Events.Count - 1);
    }
    private void Save()
    {
        if (!loaded) return;
        workspace.Targets = Cards.Select(x => x.Target).ToList();
        workspace.IntervalMs = Interval; workspace.TimeoutMs = Timeout;
        try { WorkspaceStore.Save(workspacePath, workspace); }
        catch (Exception ex) { Footer.Text = $"Não foi possível salvar: {ex.Message}"; }
    }
    private void ApplyTheme()
    {
        if (Application.Current is not null) Application.Current.RequestedThemeVariant = workspace.DarkTheme ? ThemeVariant.Dark : ThemeVariant.Light;
        foreach (var card in Cards) card.RefreshPresentation();
        foreach (var hop in Hops) hop.RefreshPresentation();
    }
    private void ThemeClick(object? sender, RoutedEventArgs e) { workspace.DarkTheme = !workspace.DarkTheme; ApplyTheme(); Save(); }
    private void UpdateFavorites() { FavoritePicker.ItemsSource = workspace.Favorites.ToList(); }
    private void SaveFavoriteClick(object? sender, RoutedEventArgs e)
    {
        var name = FavoriteName.Text?.Trim();
        if (string.IsNullOrEmpty(name) || Cards.Count == 0) { Footer.Text = "Informe o nome e adicione hosts antes de salvar."; return; }
        if (workspace.Favorites.Any(x => x.Name.Equals(name, StringComparison.OrdinalIgnoreCase))) { Footer.Text = "Esse nome já existe. Use outro nome para preservar o favorito."; return; }
        workspace.Favorites.Add(new(name, Cards.Select(x => x.Target).ToList())); UpdateFavorites(); Save(); Footer.Text = "Favorito salvo.";
    }
    private async void LoadFavoriteClick(object? sender, RoutedEventArgs e)
    {
        if (FavoritePicker.SelectedItem is not Favorite favorite) return;
        foreach (var card in Cards) card.Cancellation?.Cancel();
        await Task.WhenAll(Cards.Select(x => x.Running ?? Task.CompletedTask));
        Cards.Clear(); foreach (var target in favorite.Targets) Cards.Add(new(target)); Save();
    }
    private async void ImportClick(object? sender, RoutedEventArgs e)
    {
        try
        {
            var files = await StorageProvider.OpenFilePickerAsync(new() { Title = "Importar configuração original", AllowMultiple = false, FileTypeFilter = [new("vmPing XML") { Patterns = ["*.xml"] }] });
            if (files.Count == 0) return;
            var path = files[0].TryGetLocalPath() ?? throw new IOException("Selecione um arquivo local.");
            var imported = WorkspaceStore.ImportLegacy(path);
            foreach (var favorite in imported.Favorites)
            {
                var name = favorite.Name; var suffix = 2;
                while (workspace.Favorites.Any(x => x.Name.Equals(name, StringComparison.OrdinalIgnoreCase))) name = $"{favorite.Name} ({suffix++})";
                workspace.Favorites.Add(favorite with { Name = name });
            }
            foreach (var pair in imported.Aliases) workspace.Aliases[pair.Key] = pair.Value;
            if (Cards.Count == 0) foreach (var target in imported.Targets) Cards.Add(new(target));
            UpdateFavorites(); Save(); Footer.Text = $"{imported.Favorites.Count} favoritos importados. XML original preservado; credenciais não importadas.";
        }
        catch (Exception ex) { Footer.Text = $"Falha ao importar: {ex.Message}"; }
    }
    private async void TraceClick(object? sender, RoutedEventArgs e)
    {
        if (traceTask is { IsCompleted: false } || closing) return;
        var host = TraceHost.Text?.Trim() ?? "";
        try { var target = Target.Parse(host); if (target.Port is not null) throw new FormatException("Informe um host sem porta."); host = target.Host; }
        catch (Exception ex) { TraceMessage.Text = ex.Message; return; }
        Hops.Clear(); TraceStart.IsEnabled = false; TraceStop.IsEnabled = true;
        traceCancellation = new();
        var family = FamilyPicker.SelectedIndex == 0 ? AddressFamily.InterNetwork : AddressFamily.InterNetworkV6;
        var continuous = MtrInput.IsChecked == true;
        var maxHops = (int)(MaxHops.Value ?? 30); var timeout = Timeout; var interval = Interval;
        TraceMessage.Text = "Resolvendo destino e iniciando sondagens…";
        traceTask = RunTrace(host, family, continuous, maxHops, timeout, interval, traceCancellation.Token);
        await traceTask;
    }
    private async Task RunTrace(string host, AddressFamily family, bool continuous, int maxHops, int timeout, int interval, CancellationToken token)
    {
        try
        {
            await Task.Run(() => TraceService.RunAsync(host, family, continuous, maxHops, timeout, interval,
                result => Dispatcher.UIThread.Post(() =>
                {
                    while (Hops.Count < result.Ttl) Hops.Add(new(Hops.Count + 1));
                    Hops[result.Ttl - 1].Add(result);
                }), address => Dispatcher.UIThread.Post(() => TraceMessage.Text = $"{host} → {address} · {(continuous ? "MTR em execução" : "Traceroute em execução")} · tempos em ms"), token), token);
            TraceMessage.Text += " · Concluído";
        }
        catch (OperationCanceledException) { TraceMessage.Text = "Sondagens interrompidas. Resultados preservados."; }
        catch (Exception ex) { TraceMessage.Text = $"Erro: {ex.InnerException?.Message ?? ex.Message}"; }
        finally { TraceStart.IsEnabled = true; TraceStop.IsEnabled = false; traceCancellation?.Dispose(); traceCancellation = null; }
    }
    private void TraceStopClick(object? sender, RoutedEventArgs e) => traceCancellation?.Cancel();
    private static string Csv(object? value) => "\"" + Convert.ToString(value, CultureInfo.InvariantCulture)?.Replace("\"", "\"\"") + "\"";
    private async void ExportClick(object? sender, RoutedEventArgs e)
    {
        try
        {
            var file = await StorageProvider.SaveFilePickerAsync(new() { Title = "Exportar resultados", SuggestedFileName = $"vmping-{DateTime.Now:yyyyMMdd-HHmmss}.csv", DefaultExtension = "csv" });
            if (file is null) return;
            var text = new StringBuilder("tipo,host,alias,enviados,recebidos,perda_percentual,erros,min_ms,media_ms,max_ms\n");
            foreach (var card in Cards)
            {
                var s = card.Statistics;
                text.AppendLine(string.Join(",", new object?[] { card.Target.Kind, card.Target.Address, card.Target.Alias, s.Sent, s.Received, s.LossPercent, s.Errors, s.Received == 0 ? null : s.Min, s.Average, s.Max }.Select(Csv)));
            }
            text.AppendLine("\nsalto,ip,dns,enviados,perda,ultimo_ms,media_ms,min_ms,max_ms");
            foreach (var hop in Hops) text.AppendLine(string.Join(",", new object?[] { hop.Ttl, hop.Address, hop.DnsName, hop.Sent, hop.Loss, hop.Last, hop.Average, hop.Min, hop.Max }.Select(Csv)));
            text.AppendLine("\nevento"); foreach (var item in Events) text.AppendLine(Csv(item));
            await using var stream = await file.OpenWriteAsync(); stream.SetLength(0);
            await using var writer = new StreamWriter(stream, new UTF8Encoding(true)); await writer.WriteAsync(text.ToString()); Footer.Text = "CSV exportado.";
        }
        catch (Exception ex) { Footer.Text = $"Falha ao exportar: {ex.Message}"; }
    }
}
