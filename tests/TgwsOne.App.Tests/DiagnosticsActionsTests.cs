using System.Net;
using System.Net.Sockets;
using TgwsOne.Core;
using TgwsOne.Services;
using TgwsOne.ViewModels;

namespace TgwsOne.Tests;

/// <summary>The real <see cref="DiagnosticsActions"/> over the real Home and Bypass view-models: the verdict button does what the same button there does.</summary>
public class DiagnosticsActionsTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private sealed class Rig : IAsyncDisposable
    {
        public HomeRig Home { get; } = new();
        public FakeBypassService Service { get; } = new();
        public FakeProbes Probes { get; }
        public BypassViewModel Bypass { get; }
        public DiagnosticsActions Actions { get; }

        public Rig()
        {
            Probes = new FakeProbes(Service);
            Bypass = new BypassViewModel(Service, Home.Monitor, Home.Dialogs,
                () => new FinderViewModel(new StrategyFinder(Service, Probes, Probes, (_, _) => Task.CompletedTask)),
                _ => throw new NotSupportedException("these tests never open the strategy editor"), new FakeElevatedHelper(), new FakeClipboard());
            Actions = new DiagnosticsActions(Home.Home, Bypass, Service, Home.Monitor);
        }

        public ValueTask DisposeAsync() => Home.DisposeAsync();
    }

    [Fact]
    public async Task Connect_IsTheHomePagesConnect()
    {
        await using var rig = new Rig();

        string? problem = await rig.Actions.ConnectAsync();

        Assert.Null(problem);
        Assert.Equal(1, rig.Home.Proxy.Starts);
        Assert.True(rig.Home.Home.IsProxyRunning);
    }

    [Fact]
    public async Task Connect_ThatFails_ReturnsTheBannerText()
    {
        await using var rig = new Rig();
        rig.Home.Proxy.NextStart = new StartResult(StartOutcome.Timeout, 1443);

        string? problem = await rig.Actions.ConnectAsync();

        Assert.Contains("порт 1443 не слушается", problem);
    }

    [Fact]
    public async Task Connect_OnAPortSwitch_IsNotAFailure()
    {
        await using var rig = new Rig();
        rig.Home.Proxy.NextStart = new StartResult(StartOutcome.PortSwitched, 1444, OldPort: 1443);

        Assert.Null(await rig.Actions.ConnectAsync());   // the banner on Home explains; the check that follows says the rest
    }

    [Fact]
    public async Task OpenInTelegram_IsTheHomePagesLink()
    {
        await using var rig = new Rig();

        string? problem = await rig.Actions.OpenInTelegramAsync();

        Assert.Null(problem);
        Assert.Equal([rig.Home.Settings.Link], rig.Home.Launcher.Opened);
    }

    [Fact]
    public async Task OpenInTelegram_WhenNothingOpensIt_SaysSo()
    {
        await using var rig = new Rig();
        rig.Home.Launcher.Succeeds = false;

        string? problem = await rig.Actions.OpenInTelegramAsync();

        Assert.Contains("Не удалось открыть Telegram", problem);
    }

    [Fact]
    public async Task EnableBypass_SendsOn()
    {
        await using var rig = new Rig();
        rig.Service.Enabled = false;

        string? problem = await rig.Actions.EnableBypassAsync();

        Assert.Null(problem);
        Assert.True(rig.Service.Enabled);
        Assert.Equal(1, rig.Service.Count("on"));
    }

    [Fact]
    public async Task EnableBypass_ThatIsRefused_ReturnsTheServicesWords()
    {
        await using var rig = new Rig();
        rig.Service.Enabled = false;
        rig.Service.SwitchRefusal = "проверка параметров не прошла";

        Assert.Equal("Обход DPI не включился: проверка параметров не прошла", await rig.Actions.EnableBypassAsync());
    }

    [Fact]
    public async Task EnableBypass_WithNoService_SaysSo_InsteadOfThrowing()
    {
        await using var rig = new Rig();
        rig.Service.Unavailable = true;

        Assert.Equal("служба обхода DPI недоступна", await rig.Actions.EnableBypassAsync());
    }

    [Fact]
    public async Task FindStrategy_OpensTheFinderOfTheBypassPage()
    {
        await using var rig = new Rig();
        await rig.Home.Monitor.RefreshNowAsync();

        string? problem = await rig.Actions.FindStrategyAsync();

        Assert.Null(problem);
        Assert.Equal(1, rig.Home.Dialogs.FindersShown);
    }

    // ----- the real TCP probe -----

    [Fact]
    public async Task TcpProbe_AListeningPort_Connects()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        try
        {
            int port = ((IPEndPoint)listener.LocalEndpoint).Port;

            Assert.True(await new SocketTcpProbe().ConnectAsync("127.0.0.1", port, TimeSpan.FromSeconds(5), Ct));
        }
        finally
        {
            listener.Stop();
        }
    }

    [Fact]
    public async Task TcpProbe_AClosedPort_DoesNot()
    {
        var l = new TcpListener(IPAddress.Loopback, 0);
        l.Start();
        int port = ((IPEndPoint)l.LocalEndpoint).Port;
        l.Stop();

        Assert.False(await new SocketTcpProbe().ConnectAsync("127.0.0.1", port, TimeSpan.FromSeconds(5), Ct));
    }

    [Fact]
    public async Task TcpProbe_AnUnknownName_DoesNot_AndDoesNotThrow()
    {
        Assert.False(await new SocketTcpProbe().ConnectAsync("no-such-host.invalid", 443, TimeSpan.FromSeconds(5), Ct));
    }

    [Fact]
    public async Task TcpProbe_WhenTheCallerCancels_TheCancellationIsPassedOn()
    {
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new SocketTcpProbe().ConnectAsync("127.0.0.1", 9, TimeSpan.FromSeconds(30), cts.Token));
    }
}
