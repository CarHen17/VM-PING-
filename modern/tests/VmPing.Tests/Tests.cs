using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Diagnostics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using VmPing.Core;
using VmPing.Desktop;
using Xunit;
[assembly: AvaloniaTestApplication(typeof(VmPing.Tests.TestApp))]
namespace VmPing.Tests;

public static class TestApp
{
    public static AppBuilder BuildAvaloniaApp() => AppBuilder.Configure<App>().UseSkia().WithInterFont().UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false });
}
public sealed class CoreTests
{
    [Theory]
    [InlineData("::1", "::1", null)]
    [InlineData("[::1]:443", "::1", 443)]
    [InlineData("example.org:80", "example.org", 80)]
    [InlineData("127.0.0.1", "127.0.0.1", null)]
    public void ParsesTargets(string input, string host, int? port)
    { var target = Target.Parse(input); Assert.Equal(host, target.Host); Assert.Equal(port, target.Port); }
    [Theory]
    [InlineData("host:65536")]
    [InlineData("https://example.org")]
    [InlineData("[broken]:80")]
    [InlineData("")]
    public void RejectsInvalidTargets(string input) => Assert.Throws<FormatException>(() => Target.Parse(input));
    [Fact]
    public void LocalErrorsAreNotPacketLoss()
    {
        var stats = new Statistics();
        stats.Add(new(DateTimeOffset.Now, ResultKind.Success, 20, ""));
        stats.Add(new(DateTimeOffset.Now, ResultKind.Timeout, null, ""));
        stats.Add(new(DateTimeOffset.Now, ResultKind.LocalError, null, ""));
        Assert.Equal(50, stats.LossPercent); Assert.Equal(1, stats.Errors); Assert.Equal(20, stats.Average);
    }
    [Fact]
    public void LegacyImportPreservesSourceAndLoadsIpv6Aliases()
    {
        var path = Path.GetTempFileName();
        const string xml = "<vmping><aliases><alias host='[::1]:443'>Local</alias></aliases><favorites><favorite title='Rede' columncount='2'><host>[::1]:443</host><host>localhost</host></favorite></favorites></vmping>";
        try
        {
            File.WriteAllText(path, xml); var result = WorkspaceStore.ImportLegacy(path);
            Assert.Equal(xml, File.ReadAllText(path)); Assert.Equal("Local", result.Targets[0].Alias);
            Assert.Equal(443, result.Targets[0].Port); Assert.Single(result.Favorites);
        }
        finally { File.Delete(path); }
    }
    [Fact]
    public void SaveKeepsRecoverablePreviousVersion()
    {
        var directory = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
        var path = Path.Combine(directory, "workspace.json");
        try
        {
            WorkspaceStore.Save(path, new() { Targets = [new("localhost")] });
            WorkspaceStore.Save(path, new() { Targets = [new("::1")] });
            Assert.Equal("::1", WorkspaceStore.Load(path).Targets[0].Host);
            Assert.Equal("localhost", WorkspaceStore.Load(path + ".bak").Targets[0].Host);
        }
        finally { Directory.Delete(directory, true); }
    }
    [Fact]
    public async Task TcpAcceptRefuseAndCancellation()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        try { Assert.Equal(ResultKind.Success, (await NetworkProbe.SendAsync(new("127.0.0.1", port), 1000, default)).Kind); }
        finally { listener.Stop(); }
        Assert.Equal(ResultKind.Unreachable, (await NetworkProbe.SendAsync(new("127.0.0.1", port), 1000, default)).Kind);
        using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => NetworkProbe.SendAsync(new("127.0.0.1", port), 1000, cancellation.Token));
    }
    [Theory]
    [InlineData("127.0.0.1", AddressFamily.InterNetwork)]
    [InlineData("::1", AddressFamily.InterNetworkV6)]
    public async Task TraceResolvesAndReachesLoopback(string host, AddressFamily family)
    {
        var hops = new List<HopResult>(); IPAddress? resolved = null;
        await TraceService.RunAsync(host, family, false, 4, 500, 100, hops.Add, x => resolved = x, default);
        Assert.Equal(IPAddress.Parse(host), resolved); Assert.Single(hops);
        Assert.Equal(IPStatus.Success, hops[0].Status); Assert.NotNull(hops[0].Milliseconds);
    }
    [Fact]
    public async Task MtrRepeatsAndStopsOnCancellation()
    {
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var hops = new List<HopResult>();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => TraceService.RunAsync("127.0.0.1", AddressFamily.InterNetwork,
            true, 4, 500, 100, x => { hops.Add(x); if (hops.Count == 3) cancellation.Cancel(); }, _ => { }, cancellation.Token));
        Assert.Equal(3, hops.Count); Assert.All(hops, x => Assert.Equal(1, x.Ttl));
    }
    [Fact]
    public async Task ProbeLoopHonorsCadenceAndCancels()
    {
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var times = new List<long>();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => NetworkProbe.RunAsync(new("127.0.0.1"), 150, 500,
            _ => { times.Add(Stopwatch.GetTimestamp()); if (times.Count == 3) cancellation.Cancel(); }, cancellation.Token));
        Assert.Equal(3, times.Count);
        Assert.True(Stopwatch.GetElapsedTime(times[0], times[2]).TotalMilliseconds >= 240);
    }
    [Fact]
    public void HopAggregatesEcmpPathsAndPreservesDnsOnTimeout()
    {
        var hop = new Hop(1);
        hop.Add(new(1, IPAddress.Parse("192.0.2.1"), "first.example", 10, IPStatus.TtlExpired));
        hop.Add(new(1, null, "", null, IPStatus.TimedOut));
        Assert.Equal("first.example", hop.DnsName); Assert.Equal("50.0%", hop.Loss);
        hop.Add(new(1, IPAddress.Parse("192.0.2.2"), "second.example", 20, IPStatus.TtlExpired));
        Assert.Equal(3, hop.Sent); Assert.Equal("15.0", hop.Average); Assert.Equal(2, hop.PathCount);
        Assert.Contains("ECMP: 2", hop.Subtitle);
    }
    [Fact]
    public void StatusTrackerDebouncesFailuresAndRecovery()
    {
        var tracker = new StatusTracker(3, 2);
        Assert.True(tracker.Add(ResultKind.Success)); Assert.Equal(HostStatus.Online, tracker.Status);
        Assert.False(tracker.Add(ResultKind.Timeout)); Assert.False(tracker.Add(ResultKind.Timeout));
        Assert.Equal(HostStatus.Online, tracker.Status);
        Assert.True(tracker.Add(ResultKind.Timeout)); Assert.Equal(HostStatus.Down, tracker.Status);
        Assert.False(tracker.Add(ResultKind.Success)); Assert.Equal(HostStatus.Down, tracker.Status);
        Assert.True(tracker.Add(ResultKind.Success)); Assert.Equal(HostStatus.Online, tracker.Status);
        tracker.Stop(); Assert.True(tracker.Add(ResultKind.LocalError)); Assert.Equal(HostStatus.LocalError, tracker.Status);
    }
    [Fact]
    public void StatisticsComputeJitter()
    {
        var stats = new Statistics();
        foreach (var ms in new double[] { 10, 14, 12 }) stats.Add(new(DateTimeOffset.Now, ResultKind.Success, ms, ""));
        Assert.Equal(3, stats.Jitter, 3);
    }
    [Theory]
    [InlineData(0, 0.23, 0.23)]
    [InlineData(12, 12.7, 12.7)]
    [InlineData(0, 4.5, 0)]
    [InlineData(5, 3.0, 5)]
    public void PreciseRoundtripOnlyRefinesWithinReportedMillisecond(long reported, double elapsed, double expected) =>
        Assert.Equal(expected, NetworkProbe.PreciseRoundtrip(reported, elapsed), 3);
    [Fact]
    public async Task IcmpLoopbackHasSubMillisecondPrecisionOrReportedValue()
    {
        var result = await NetworkProbe.SendAsync(new("127.0.0.1"), 1000, default);
        Assert.Equal(ResultKind.Success, result.Kind); Assert.InRange(result.Milliseconds!.Value, 0, 1000);
    }
    [Fact]
    public async Task ProbeLoopReportsDnsFailureAndKeepsRunning()
    {
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var results = new List<ProbeResult>();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => NetworkProbe.RunAsync(new("nao-existe.invalid"), 100, 500,
            x => { results.Add(x); if (results.Count == 2) cancellation.Cancel(); }, cancellation.Token));
        Assert.Equal(2, results.Count); Assert.All(results, x => Assert.Equal(ResultKind.LocalError, x.Kind));
    }
    [Fact]
    public async Task AlertBatcherGroupsMessages()
    {
        var sent = new List<string>();
        var batcher = new AlertBatcher(x => { sent.Add(x); return Task.CompletedTask; }, TimeSpan.FromMilliseconds(100));
        batcher.Add("a"); batcher.Add("b");
        await batcher.Flush();
        Assert.Single(sent); Assert.Equal("a\nb", sent[0]);
    }
    [Fact]
    public void HistoryStoreRotatesAndLoadsNewestFirst()
    {
        var directory = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
        try
        {
            var store = new HistoryStore(Path.Combine(directory, "history.log"), 50);
            for (var i = 0; i < 10; i++) store.Append($"evento {i}");
            Assert.True(File.Exists(store.Path + ".1"));
            var recent = store.LoadRecent(2);
            Assert.Equal(["evento 9", "evento 8"], recent);
        }
        finally { Directory.Delete(directory, true); }
    }
    [Fact]
    public void DegradationFlagsSlowAndLossyHosts()
    {
        var monitor = new DegradationMonitor { LatencyMs = 50, LossPercent = 10, Window = 10, MinimumSamples = 5 };
        for (var i = 0; i < 4; i++) Assert.False(monitor.Add(ResultKind.Success, 80));
        Assert.True(monitor.Add(ResultKind.Success, 80)); Assert.Contains("latência", monitor.Reason);
        for (var i = 0; i < 10; i++) monitor.Add(ResultKind.Success, 10);
        Assert.False(monitor.IsDegraded);
        monitor.Add(ResultKind.Timeout, null); monitor.Add(ResultKind.Timeout, null);
        Assert.True(monitor.IsDegraded); Assert.Contains("perda", monitor.Reason);
        monitor.Add(ResultKind.LocalError, null);
        Assert.Equal(20, monitor.RecentLoss, 3);
    }
    [Fact]
    public void CardReportsDegradedOnlyWhileOnline()
    {
        var card = new Card(new("192.0.2.10"));
        card.Degradation.LatencyMs = 50; card.Degradation.MinimumSamples = 2;
        card.SetResult(new(DateTimeOffset.Now, ResultKind.Success, 100, ""));
        Assert.Equal(HostStatus.Online, card.Status);
        Assert.True(card.SetResult(new(DateTimeOffset.Now, ResultKind.Success, 100, "")));
        Assert.Equal(HostStatus.Degraded, card.Status); Assert.True(card.IsProblem);
        for (var i = 0; i < 3; i++) card.SetResult(new(DateTimeOffset.Now, ResultKind.Timeout, null, ""));
        Assert.Equal(HostStatus.Down, card.Status);
    }
    [Fact]
    public void SampleStoreAggregatesBuckets()
    {
        var directory = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
        try
        {
            var store = new SampleStore(directory);
            var target = new Target("192.0.2.1", 443);
            var start = new DateTimeOffset(2026, 10, 15, 12, 0, 0, TimeSpan.Zero);
            store.Add(target, new(start, ResultKind.Success, 10, ""));
            store.Add(target, new(start.AddSeconds(1), ResultKind.Success, 30, ""));
            store.Add(target, new(start.AddSeconds(2), ResultKind.Timeout, null, ""));
            store.Add(target, new(start.AddSeconds(3), ResultKind.LocalError, null, ""));
            store.Add(target, new(start.AddMinutes(30), ResultKind.Success, 5, ""));
            store.Add(new Target("192.0.2.1"), new(start, ResultKind.Success, 99, ""));
            var buckets = store.Load(target, start, start.AddHours(1), 2);
            Assert.Equal(2, buckets.Count);
            Assert.Equal(4, buckets[0].Sent); Assert.Equal(2, buckets[0].Received); Assert.Equal(1, buckets[0].Errors);
            Assert.Equal(20, buckets[0].Average); Assert.Equal(30, buckets[0].Maximum); Assert.Equal(100d / 3, buckets[0].LossPercent, 3);
            Assert.Equal(5, buckets[1].Average);
            store.Cleanup(7, start.AddDays(10));
            Assert.Empty(store.Load(target, start, start.AddHours(1), 2));
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
    }
    [Fact]
    public void IcmpChecksumMatchesReference()
    {
        // Echo request id 0x1234 seq 1 without payload: checksum computed by hand.
        byte[] packet = [8, 0, 0, 0, 0x12, 0x34, 0x00, 0x01];
        Assert.Equal(0xE5CA, RawIcmpTracer.Checksum(packet));
    }
    [Fact]
    public async Task RawTracerReachesLoopbackWhenAvailable()
    {
        using var raw = RawIcmpTracer.TryCreate(AddressFamily.InterNetwork);
        if (raw is null) return; // Needs root or CAP_NET_RAW on Linux.
        var hops = await raw.ProbeAsync(IPAddress.Loopback, 3, 1000, default);
        Assert.Equal(IPStatus.Success, hops[0].Status); Assert.Equal(IPAddress.Loopback, hops[0].Address);
    }
    [AvaloniaFact]
    public void WindowRendersAndNavigatesWithoutStartingProbes()
    {
        var path = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString(), "workspace.json");
        var window = new MainWindow(path); window.Show();
        Assert.Equal("vmPing Modern · Preview", window.Title);
        Assert.NotNull(window.FindControl<Avalonia.Controls.TextBox>("TraceHost"));
        window.FindControl<Button>("NavTrace")!.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
        Assert.True(window.FindControl<Grid>("TracePage")!.IsVisible);
        Assert.False(window.FindControl<Grid>("MonitorPage")!.IsVisible);
        var card = new Card(new("127.0.0.1")); window.Cards.Add(card);
        Assert.Equal("1", window.FindControl<TextBlock>("TotalCount")!.Text);
        Assert.Null(card.Running);
        card.SetResult(new(DateTimeOffset.Now, ResultKind.Success, 5, "Local"));
        Assert.Equal("1", window.FindControl<TextBlock>("OnlineCount")!.Text);
        card.SetResult(new(DateTimeOffset.Now, ResultKind.Timeout, null, "Local"));
        Assert.Equal("1", window.FindControl<TextBlock>("OnlineCount")!.Text);
        card.Stopped();
        Assert.Equal("0", window.FindControl<TextBlock>("OnlineCount")!.Text);
        window.FindControl<TextBox>("HostInput")!.Text = "127.0.0.2 OLT Centro, 127.0.0.3";
        window.FindControl<TextBox>("AliasInput")!.Text = "Ignorado";
        window.FindControl<TextBox>("HostInput")!.RaiseEvent(new Avalonia.Input.KeyEventArgs { RoutedEvent = Avalonia.Input.InputElement.KeyDownEvent, Key = Avalonia.Input.Key.Enter });
        Assert.Equal("OLT Centro", window.Cards.Single(x => x.Target.Host == "127.0.0.2").Title);
        Assert.Equal("127.0.0.3", window.Cards.Single(x => x.Target.Host == "127.0.0.3").Title);
        window.FindControl<TextBox>("HostInput")!.Text = "127.0.0.4 Borda";
        window.FindControl<AutoCompleteBox>("GroupInput")!.Text = "POP Centro";
        window.FindControl<TextBox>("HostInput")!.RaiseEvent(new Avalonia.Input.KeyEventArgs { RoutedEvent = Avalonia.Input.InputElement.KeyDownEvent, Key = Avalonia.Input.Key.Enter });
        Assert.Equal("POP Centro", window.Cards.Single(x => x.Target.Host == "127.0.0.4").Group);
        Assert.Contains(window.Groups, x => x.Name == "POP Centro" && x.Cards.Count == 1);
        window.FindControl<TextBox>("SearchInput")!.Text = "Borda"; Avalonia.Threading.Dispatcher.UIThread.RunJobs();
        Assert.Equal(1, window.Groups.Sum(x => x.Cards.Count));
        window.FindControl<TextBox>("SearchInput")!.Text = ""; Avalonia.Threading.Dispatcher.UIThread.RunJobs();
        Assert.Equal(window.Cards.Count, window.Groups.Sum(x => x.Cards.Count));
        window.Close();
        if (Directory.Exists(Path.GetDirectoryName(path))) Directory.Delete(Path.GetDirectoryName(path)!, true);
    }
}
