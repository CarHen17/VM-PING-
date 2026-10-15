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
    public void HopStatisticsResetOnRouteChangeAndPreserveDnsOnTimeout()
    {
        var hop = new Hop(1);
        hop.Add(new(1, IPAddress.Parse("192.0.2.1"), "first.example", 10, IPStatus.TtlExpired));
        hop.Add(new(1, null, "", null, IPStatus.TimedOut));
        Assert.Equal("first.example", hop.DnsName); Assert.Equal("50.0%", hop.Loss);
        hop.Add(new(1, IPAddress.Parse("192.0.2.2"), "second.example", 20, IPStatus.TtlExpired));
        Assert.Equal(1, hop.Sent); Assert.Equal("20.0", hop.Average);
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
        card.Stopped();
        Assert.Equal("0", window.FindControl<TextBlock>("OnlineCount")!.Text);
        window.Close();
        if (Directory.Exists(Path.GetDirectoryName(path))) Directory.Delete(Path.GetDirectoryName(path)!, true);
    }
}
