using TgwsOne.Core.Zapret;
using TgwsOne.Services;
using TgwsOne.ViewModels;

namespace TgwsOne.Tests;

/// <summary>The tray menu over the real Главная and Обход DPI view-models and monitor, with fakes underneath.</summary>
internal sealed class TrayRig : IAsyncDisposable
{
    public FakeProxyControl Proxy { get; } = new();
    public FakeBypassService Service { get; } = new();
    public FakeNetInfo Net { get; } = new();
    public FakeAppSettings Settings { get; } = new();
    public FakeLauncher Launcher { get; } = new();
    public FakeClipboard Clipboard { get; } = new();
    public FakeDialogs Dialogs { get; } = new();
    public StatusMonitor Monitor { get; }
    public HomeViewModel Home { get; }
    public BypassViewModel Bypass { get; }
    public TrayViewModel Tray { get; }
    public int Shown { get; private set; }
    public int Quit { get; private set; }
    public int Changes { get; private set; }

    public TrayRig()
    {
        Monitor = new StatusMonitor(Proxy, Service, Net, TimeSpan.FromHours(1));
        Home = new HomeViewModel(Proxy, Service, Monitor, Settings, Launcher, Clipboard, Dialogs, new FakeNavigator(), () => new QrViewModel(Settings, Net, Clipboard));
        Bypass = new BypassViewModel(Service, Monitor, Dialogs, () => throw new NotSupportedException(), _ => throw new NotSupportedException(),
                                     new FakeElevatedHelper(), Clipboard);
        Tray = new TrayViewModel(Home, Bypass, Monitor, () => Shown++, () => Quit++);
        Tray.Changed += () => Changes++;
    }

    public IReadOnlyList<string> Texts => [.. Tray.Items.Select(i => i.IsSeparator ? "-" : i.Text)];

    public TrayItem Item(string text) => Tray.Items.Single(i => i.Text == text);

    /// <summary>Waits for the strategies the tray loads by itself once it sees the service.</summary>
    public async Task WaitForStrategiesAsync()
    {
        DateTime limit = DateTime.UtcNow.AddSeconds(5);
        while (Bypass.Strategies.Count == 0)
        {
            if (DateTime.UtcNow > limit) throw new TimeoutException("the strategies never loaded");
            await Task.Delay(10, TestContext.Current.CancellationToken);
        }
    }

    public ValueTask DisposeAsync() => Monitor.DisposeAsync();
}

public class TrayViewModelTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static string[] Strategies(TrayItem bypassMenu) => [.. bypassMenu.Children!.Where(c => c.IsRadio).Select(c => c.Text)];

    // ----- the tooltip and the dot -----

    [Fact]
    public async Task TheTooltipAndTheDot_AreTheHeadlineOfGlavnaya()
    {
        await using var rig = new TrayRig();
        await rig.Monitor.RefreshNowAsync();

        Assert.Equal(rig.Home.Headline, rig.Tray.Tooltip);
        Assert.Equal(rig.Home.HeadlineTone, rig.Tray.Tone);
        Assert.Equal("Отключено", rig.Tray.Tooltip);
        Assert.Equal(Tone.Grey, rig.Tray.Tone);

        await rig.Home.ConnectCommand.ExecuteAsync(null);

        Assert.Equal(rig.Home.Headline, rig.Tray.Tooltip);
        Assert.NotEqual("Отключено", rig.Tray.Tooltip);
        Assert.Equal(rig.Home.HeadlineTone, rig.Tray.Tone);
    }

    // ----- the menu -----

    [Fact]
    public async Task TheMenu_HasTheItemsOfTheSpec_InOrder()
    {
        await using var rig = new TrayRig();
        await rig.Monitor.RefreshNowAsync();

        Assert.Equal(["Показать", "-", "Подключить", "Открыть в Telegram", "Копировать ссылку", "Обход DPI", "-", "Выход"], rig.Texts);
    }

    [Fact]
    public async Task WhileTheProxyRuns_ConnectBecomesDisconnect()
    {
        await using var rig = new TrayRig();
        await rig.Item("Подключить").Command!.ExecuteAsync();   // the page's own command
        await rig.Monitor.RefreshNowAsync();

        Assert.Contains("Отключить", rig.Texts);
        Assert.DoesNotContain("Подключить", rig.Texts);

        await rig.Item("Отключить").Command!.ExecuteAsync();

        Assert.Equal(1, rig.Proxy.Stops);
        Assert.Contains("Подключить", rig.Texts);
    }

    [Fact]
    public async Task Connect_IsTheCommandOfTheHomePage_NotACopy()
    {
        await using var rig = new TrayRig();

        Assert.Same(rig.Home.ConnectCommand, rig.Item("Подключить").Command);
        Assert.Same(rig.Home.OpenInTelegramCommand, rig.Item("Открыть в Telegram").Command);
        Assert.Same(rig.Home.CopyLinkCommand, rig.Item("Копировать ссылку").Command);
    }

    [Fact]
    public async Task ShowAndQuit_CallBack()
    {
        await using var rig = new TrayRig();

        rig.Item("Показать").Command!.Execute(null);
        rig.Item("Выход").Command!.Execute(null);

        Assert.Equal(1, rig.Shown);
        Assert.Equal(1, rig.Quit);
    }

    [Fact]
    public async Task OpenInTelegram_AndCopyLink_DoWhatTheButtonsDo()
    {
        await using var rig = new TrayRig();

        await rig.Item("Открыть в Telegram").Command!.ExecuteAsync();
        await rig.Item("Копировать ссылку").Command!.ExecuteAsync();

        Assert.Equal([rig.Settings.Link], rig.Launcher.Opened);
        Assert.Equal(rig.Settings.Link, rig.Clipboard.Text);
    }

    // ----- Обход DPI -----

    [Fact]
    public async Task WithoutTheService_TheBypassMenuIsThereButDisabled()
    {
        await using var rig = new TrayRig();
        rig.Service.Unavailable = true;
        await rig.Monitor.RefreshNowAsync();

        TrayItem bypass = rig.Item("Обход DPI");

        Assert.False(bypass.IsEnabled);
        Assert.Null(bypass.Children);
    }

    [Fact]
    public async Task WithTheService_TheTrayLoadsTheStrategiesItself_AndListsThemAsRadios()
    {
        await using var rig = new TrayRig();
        rig.Service.Strategy = "split";
        await rig.Monitor.RefreshNowAsync();   // the page was never shown: only the tray is looking
        await rig.WaitForStrategiesAsync();

        TrayItem bypass = rig.Item("Обход DPI");

        Assert.True(bypass.IsEnabled);
        Assert.Equal("Выключить", bypass.Children![0].Text);
        Assert.Same(rig.Bypass.ToggleBypassCommand, bypass.Children![0].Command);
        Assert.Equal(new[] { "auto", "fake-tls", "split", "split-stealth", "aggressive" }.Select(StatusText.DisplayName), Strategies(bypass));
        Assert.Equal([StatusText.DisplayName("split")], bypass.Children!.Where(c => c.IsChecked).Select(c => c.Text));
    }

    [Fact]
    public async Task TheToggle_SaysWhatItWillDo()
    {
        await using var rig = new TrayRig();
        rig.Service.Enabled = false;
        rig.Service.State = "disabled";
        await rig.Monitor.RefreshNowAsync();

        Assert.Equal("Включить", rig.Item("Обход DPI").Children![0].Text);

        await rig.Item("Обход DPI").Children![0].Command!.ExecuteAsync();
        await rig.Monitor.RefreshNowAsync();

        Assert.Equal("Выключить", rig.Item("Обход DPI").Children![0].Text);
        Assert.Contains(rig.Service.Sent, r => r.Cmd == ControlCommands.On);
    }

    [Fact]
    public async Task ChoosingAStrategy_SendsUse_ThroughThePagesOwnCommand()
    {
        await using var rig = new TrayRig();
        await rig.Monitor.RefreshNowAsync();
        await rig.WaitForStrategiesAsync();
        TrayItem split = rig.Item("Обход DPI").Children!.Single(c => c.Text == "split");

        await split.Command!.ExecuteAsync();

        Assert.Contains(rig.Service.Sent, r => r is { Cmd: ControlCommands.Use, Strategy: "split" });
    }

    // ----- change -----

    [Fact]
    public async Task EverySnapshot_TellsTheTrayToLookAgain_AndTheSignatureFollowsWhatShows()
    {
        await using var rig = new TrayRig();
        await rig.Monitor.RefreshNowAsync();
        string before = rig.Tray.Signature;
        int changes = rig.Changes;

        await rig.Home.ConnectCommand.ExecuteAsync(null);

        Assert.True(rig.Changes > changes);
        Assert.NotEqual(before, rig.Tray.Signature);
        await rig.Monitor.RefreshNowAsync();
        string steady = rig.Tray.Signature;
        await rig.Monitor.RefreshNowAsync();
        Assert.Equal(steady, rig.Tray.Signature);   // nothing changed, so the platform's menu need not be rebuilt
    }

    [Fact]
    public async Task AStrategyListThatChanges_TellsTheTrayToo()
    {
        await using var rig = new TrayRig();
        await rig.Monitor.RefreshNowAsync();
        await rig.WaitForStrategiesAsync();
        int changes = rig.Changes;
        string before = rig.Tray.Signature;

        rig.Bypass.Strategies.Add(new StrategyOption("custom:mine", _ => Task.CompletedTask));   // a custom strategy was saved on the page

        Assert.Equal(changes + 1, rig.Changes);
        Assert.NotEqual(before, rig.Tray.Signature);
        Assert.Contains(StatusText.DisplayName("custom:mine"), Strategies(rig.Item("Обход DPI")));
    }

    // ----- the plain data -----

    [Fact]
    public void ASignature_CoversWhatShows()
    {
        var a = new TrayItem("Обход DPI", Children: [new("auto", IsRadio: true, IsChecked: true), new("split", IsRadio: true)]);
        var b = new TrayItem("Обход DPI", Children: [new("auto", IsRadio: true), new("split", IsRadio: true, IsChecked: true)]);
        var disabled = new TrayItem("Обход DPI", IsEnabled: false);

        Assert.NotEqual(a.Signature, b.Signature);
        Assert.NotEqual(a.Signature, disabled.Signature);
        Assert.Equal(a.Signature, new TrayItem("Обход DPI", Children: [new("auto", IsRadio: true, IsChecked: true), new("split", IsRadio: true)]).Signature);
        Assert.Equal("-", TrayItem.Separator.Signature);
    }
}

internal static class CommandExtensions
{
    /// <summary>Runs an async command the way a menu click does, and waits for it.</summary>
    public static async Task ExecuteAsync(this System.Windows.Input.ICommand command)
    {
        if (command is CommunityToolkit.Mvvm.Input.IAsyncRelayCommand async) await async.ExecuteAsync(null);
        else command.Execute(null);
    }
}
