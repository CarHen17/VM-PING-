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
    private ProbeSettings probeSettings = new(1000, 1000);
    private readonly HistoryStore history;
    private readonly AlertBatcher alerts;
    public MainWindow() : this(WorkspaceStore.DefaultPath) { }
    public MainWindow(string path)
    {
        workspacePath = path;
        history = new(Path.Combine(Path.GetDirectoryName(Path.GetFullPath(path))!, "history.log"));
        alerts = new(SendTelegramAsync, TimeSpan.FromSeconds(5));
        alerts.Failed += ex => Dispatcher.UIThread.Post(() => Footer.Text = $"Falha ao enviar alerta: {ex.Message}");
        InitializeComponent(); DataContext = this;
        SizeChanged += (_, _) => CardColumns = Math.Clamp((int)((Bounds.Width - 274) / 335), 1, 4);
        Cards.CollectionChanged += (_, e) =>
        {
            if (e.OldItems is not null) foreach (Card card in e.OldItems) card.StatusChanged -= CardStatusChanged;
            if (e.NewItems is not null) foreach (Card card in e.NewItems) card.StatusChanged += CardStatusChanged;
            UpdateOverview();
        };
        Hops.CollectionChanged += (_, _) => TraceEmpty.IsVisible = Hops.Count == 0;
        Events.CollectionChanged += (_, _) => HistoryEmpty.IsVisible = Events.Count == 0;
        try { workspace = WorkspaceStore.Load(workspacePath); loaded = true; }
        catch (Exception ex) { Footer.Text = $"Não foi possível abrir a configuração: {ex.Message} Salvamento desativado para preservar o arquivo."; }
        IntervalInput.Value = workspace.IntervalMs; TimeoutInput.Value = workspace.TimeoutMs;
        FailuresInput.Value = workspace.FailuresToDown; SuccessesInput.Value = workspace.SuccessesToUp;
        TelegramEnabled.IsChecked = workspace.Alerts.TelegramEnabled;
        TelegramToken.Text = workspace.Alerts.TelegramToken; TelegramChat.Text = workspace.Alerts.TelegramChatId;
        UpdateProbeSettings();
        foreach (var input in new[] { IntervalInput, TimeoutInput, FailuresInput, SuccessesInput })
            input.ValueChanged += (_, _) => { UpdateProbeSettings(); Save(); };
        TelegramEnabled.IsCheckedChanged += (_, _) => Save();
        TelegramToken.LostFocus += (_, _) => Save(); TelegramChat.LostFocus += (_, _) => Save();
        try { foreach (var item in history.LoadRecent(2000)) Events.Add(item); }
        catch (Exception ex) { Footer.Text = $"Não foi possível ler o histórico: {ex.Message}"; }
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
            Save();
            await Task.WhenAny(alerts.Flush(), Task.Delay(TimeSpan.FromSeconds(8)));
            allowClose = true; Close();
        };
    }
    private void CardStatusChanged(Card card) => UpdateOverview();
    private void UpdateProbeSettings()
    {
        probeSettings = new(Interval, Timeout);
        foreach (var card in Cards) ApplyThresholds(card);
    }
    private void ApplyThresholds(Card card)
    {
        card.Tracker.FailuresToDown = (int)(FailuresInput.Value ?? 3);
        card.Tracker.SuccessesToUp = (int)(SuccessesInput.Value ?? 1);
    }
    private void UpdateOverview()
    {
        TotalCount.Text = Cards.Count.ToString();
        OnlineCount.Text = Cards.Count(x => x.Status == HostStatus.Online).ToString();
        DownCount.Text = Cards.Count(x => x.Status == HostStatus.Down).ToString();
        ErrorCount.Text = Cards.Count(x => x.Status == HostStatus.LocalError).ToString();
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
            // Each entry is "host" or "host alias"; hosts never contain spaces, so the rest of the entry is the alias.
            var entries = (HostInput.Text ?? "").Split([',', ';', '\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            var typedAlias = AliasInput.Text?.Trim() ?? "";
            var targets = entries.Select(x =>
            {
                var parts = x.Split((char[]?)null, 2, StringSplitOptions.RemoveEmptyEntries);
                var alias = parts.Length > 1 ? parts[1].Trim() : entries.Length == 1 ? typedAlias : "";
                return Target.Parse(parts[0], alias);
            }).ToList();
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
        ApplyThresholds(card);
        card.Running = RunCard(card, cancellation);
        card.RefreshPresentation();
    }
    private async Task RunCard(Card card, CancellationTokenSource cancellation)
    {
        try
        {
            await NetworkProbe.RunAsync(card.Target, () => probeSettings, result =>
            {
                var previous = card.Status;
                if (!card.SetResult(result)) return;
                AddEvent($"{card.Title} ({card.Target.Address}): {card.StatusLabel.ToUpperInvariant()} · {result.Detail}");
                Footer.Text = $"{result.Time:HH:mm:ss} · {card.Title}: {card.StatusLabel} · {result.Detail}";
                // The first result only establishes the initial state; alert on real transitions.
                if (previous != HostStatus.Stopped) Alert(card, result);
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
        var line = $"{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss zzz} · {value}";
        Events.Insert(0, line);
        if (Events.Count > 2000) Events.RemoveAt(Events.Count - 1);
        if (!loaded) return;
        try { history.Append(line); }
        catch (Exception ex) { Footer.Text = $"Não foi possível gravar o histórico: {ex.Message}"; }
    }
    private void Alert(Card card, ProbeResult result)
    {
        if (TelegramEnabled.IsChecked != true) return;
        var icon = card.Status switch { HostStatus.Online => "🟢", HostStatus.Down => "🔴", _ => "🟠" };
        alerts.Add($"{icon} {card.Title} ({card.Target.Address}) · {card.StatusLabel} · {result.Time:dd/MM HH:mm:ss}");
    }
    private Task SendTelegramAsync(string text) =>
        TelegramClient.SendAsync(workspace.Alerts.TelegramToken, workspace.Alerts.TelegramChatId, $"vmPing · {Environment.MachineName}\n{text}");
    private async void TestTelegramClick(object? sender, RoutedEventArgs e)
    {
        Save();
        try { await TelegramClient.SendAsync(TelegramToken.Text ?? "", TelegramChat.Text ?? "", $"vmPing · {Environment.MachineName}\n✅ Teste de alerta"); Footer.Text = "Mensagem de teste enviada ao Telegram."; }
        catch (Exception ex) { Footer.Text = $"Falha no teste do Telegram: {ex.Message}"; }
    }
    private void Save()
    {
        if (!loaded) return;
        workspace.Targets = Cards.Select(x => x.Target).ToList();
        workspace.IntervalMs = Interval; workspace.TimeoutMs = Timeout;
        workspace.FailuresToDown = (int)(FailuresInput.Value ?? 3); workspace.SuccessesToUp = (int)(SuccessesInput.Value ?? 1);
        workspace.Alerts.TelegramEnabled = TelegramEnabled.IsChecked == true;
        workspace.Alerts.TelegramToken = TelegramToken.Text?.Trim() ?? ""; workspace.Alerts.TelegramChatId = TelegramChat.Text?.Trim() ?? "";
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
    private void DeleteFavoriteClick(object? sender, RoutedEventArgs e)
    {
        if (FavoritePicker.SelectedItem is not Favorite favorite) { Footer.Text = "Selecione um favorito para excluir."; return; }
        workspace.Favorites.Remove(favorite); UpdateFavorites(); Save(); Footer.Text = $"Favorito \"{favorite.Name}\" excluído.";
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
                    // The path got shorter: drop rows beyond the destination.
                    if (result.Status == System.Net.NetworkInformation.IPStatus.Success)
                        while (Hops.Count > result.Ttl) Hops.RemoveAt(Hops.Count - 1);
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
            var text = new StringBuilder("tipo,host,alias,status,enviados,recebidos,perda_percentual,erros,min_ms,media_ms,max_ms,jitter_ms\n");
            foreach (var card in Cards)
            {
                var s = card.Statistics;
                text.AppendLine(string.Join(",", new object?[] { card.Target.Kind, card.Target.Address, card.Target.Alias, card.StatusLabel, s.Sent, s.Received, s.LossPercent, s.Errors, s.Received == 0 ? null : s.Min, s.Average, s.Max, s.HasJitter ? s.Jitter : null }.Select(Csv)));
            }
            text.AppendLine("\nsalto,ip,dns,enviados,perda,ultimo_ms,media_ms,min_ms,max_ms,roteadores");
            foreach (var hop in Hops) text.AppendLine(string.Join(",", new object?[] { hop.Ttl, hop.Address, hop.DnsName, hop.Sent, hop.Loss, hop.Last, hop.Average, hop.Min, hop.Max, hop.Paths.Replace("\n", " | ") }.Select(Csv)));
            text.AppendLine("\nevento"); foreach (var item in Events) text.AppendLine(Csv(item));
            await using var stream = await file.OpenWriteAsync(); stream.SetLength(0);
            await using var writer = new StreamWriter(stream, new UTF8Encoding(true)); await writer.WriteAsync(text.ToString()); Footer.Text = "CSV exportado.";
        }
        catch (Exception ex) { Footer.Text = $"Falha ao exportar: {ex.Message}"; }
    }
}
