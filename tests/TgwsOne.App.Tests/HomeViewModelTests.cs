using TgwsOne.Core;
using TgwsOne.Core.Zapret;
using TgwsOne.Services;
using TgwsOne.ViewModels;

namespace TgwsOne.Tests;

public class HomeViewModelTests
{
    // ----- what the page shows -----

    [Fact]
    public async Task Initially_ShowsAStoppedProxy_AndTheBypassLine()
    {
        await using var rig = new HomeRig();
        await rig.Monitor.RefreshNowAsync();

        Assert.Equal("Отключено", rig.Home.Headline);
        Assert.Equal(Tone.Grey, rig.Home.HeadlineTone);
        Assert.Equal("остановлен", rig.Home.ProxyLine);
        Assert.False(rig.Home.IsProxyRunning);
        Assert.Equal("включён · Авто", rig.Home.BypassLine);
        Assert.True(rig.Home.BypassOpensPage);
        Assert.Null(rig.Home.Banner);
    }

    [Fact]
    public async Task ARunningProxy_ShowsItsAddress_AndTheTelegramVerdict()
    {
        await using var rig = new HomeRig();
        rig.Proxy.Status = Samples.ProxyListening(1443);
        rig.Net.Set(Samples.Tcp(1443, 50001));

        await rig.Monitor.RefreshNowAsync();

        Assert.Equal("Telegram работает через прокси", rig.Home.Headline);
        Assert.Equal(Tone.Green, rig.Home.HeadlineTone);
        Assert.Equal("работает · 127.0.0.1:1443", rig.Home.ProxyLine);
        Assert.True(rig.Home.IsProxyRunning);
    }

    [Fact]
    public async Task APortHeldByAnotherApp_IsSaidSo()
    {
        await using var rig = new HomeRig();
        rig.Proxy.Status = new ProxyStatus(Running: false, Listening: false, Port: 1443, PortBusyByOther: true, Pid: null);

        await rig.Monitor.RefreshNowAsync();

        Assert.Equal("остановлен · порт 1443 занят другим приложением", rig.Home.ProxyLine);
        Assert.Equal(Tone.Amber, rig.Home.ProxyTone);
    }

    [Fact]
    public async Task WithoutAService_TheBypassLineDoesNotLeadAnywhere()
    {
        await using var rig = new HomeRig();
        rig.Control.Unavailable = true;

        await rig.Monitor.RefreshNowAsync();

        Assert.Equal("не установлен — нужен администратор", rig.Home.BypassLine);
        Assert.False(rig.Home.BypassOpensPage);
    }

    // ----- Подключить -----

    [Fact]
    public async Task Connect_StartsTheProxy_AndRemembersTheResult()
    {
        await using var rig = new HomeRig();

        await rig.Home.ConnectCommand.ExecuteAsync(null);

        Assert.Equal(1, rig.Proxy.Starts);
        Assert.Equal(StartOutcome.Started, rig.Monitor.Current.LastStart!.Outcome);
        Assert.True(rig.Home.IsProxyRunning);
        Assert.Equal("Прокси готов — откройте ссылку в Telegram", rig.Home.Headline);
        Assert.Null(rig.Home.Banner);
        Assert.False(rig.Home.IsBusy);
    }

    [Fact]
    public async Task Connect_OnAPortSwitch_ExplainsAndOffersTheLink()
    {
        await using var rig = new HomeRig();
        rig.Proxy.NextStart = new StartResult(StartOutcome.PortSwitched, 1444, OldPort: 1443);

        await rig.Home.ConnectCommand.ExecuteAsync(null);

        HomeBanner banner = Assert.IsType<HomeBanner>(rig.Home.Banner);
        Assert.Equal("Порт 1443 занят — прокси на 1444. Откройте ссылку в Telegram заново.", banner.Text);
        Assert.Equal(BannerAction.OpenInTelegram, banner.Action);
        Assert.Equal("Открыть в Telegram", banner.ActionText);
        Assert.True(rig.Home.IsProxyRunning);

        await rig.Home.RunBannerActionCommand.ExecuteAsync(null);

        Assert.Equal([rig.Settings.Link], rig.Launcher.Opened);
    }

    [Fact]
    public async Task Connect_WhenTheStartFails_ShowsWhyInTheHeadlineAndTheBanner_AndLeavesTheBypassAlone()
    {
        await using var rig = new HomeRig();
        rig.Control.Status = Samples.Bypass("disabled");
        rig.Proxy.NextStart = new StartResult(StartOutcome.EngineExited, 1443, ExitCode: 2, Output: "Traceback\nValueError: bad secret");

        await rig.Home.ConnectCommand.ExecuteAsync(null);

        Assert.Equal("Прокси не запустился: движок завершился с кодом 2", rig.Home.Headline);
        Assert.Equal(Tone.Red, rig.Home.HeadlineTone);
        HomeBanner banner = Assert.IsType<HomeBanner>(rig.Home.Banner);
        Assert.Equal(Tone.Red, banner.Tone);
        Assert.Contains("движок завершился с кодом 2", banner.Text);
        Assert.Contains("Traceback", banner.Text);
        Assert.DoesNotContain("ValueError", banner.Text);   // the first line only
        Assert.Equal(0, rig.Sent("on"));
    }

    [Fact]
    public async Task Connect_WhenStartingThrows_ShowsTheMessage_AndEndsTheBusyState()
    {
        await using var rig = new HomeRig();
        rig.Proxy.StartThrows = new TimeoutException("другой запуск прокси не завершился за 15 с");

        await rig.Home.ConnectCommand.ExecuteAsync(null);

        Assert.Equal("Не удалось запустить прокси: другой запуск прокси не завершился за 15 с", rig.Home.Banner!.Text);
        Assert.False(rig.Home.IsBusy);
        Assert.Equal(0, rig.Sent("on"));
    }

    [Theory]
    [InlineData("disabled", false, 1)]   // there, and off: turned on
    [InlineData("running", false, 0)]    // already on
    [InlineData("error", false, 0)]      // on, and in trouble: not ours to switch
    [InlineData("disabled", true, 0)]    // no service to ask
    public async Task Connect_TurnsTheBypassOn_OnlyWhenTheServiceIsThereAndOff(string state, bool unreachable, int expectedOn)
    {
        await using var rig = new HomeRig();
        rig.Control.Status = Samples.Bypass(state);
        rig.Control.Unavailable = unreachable;

        await rig.Home.ConnectCommand.ExecuteAsync(null);

        Assert.Equal(expectedOn, rig.Sent("on"));
    }

    [Fact]
    public async Task Connect_ShowsTheBypassAsOn_AfterTurningItOn()
    {
        await using var rig = new HomeRig();
        rig.Control.Status = Samples.Bypass("disabled");
        rig.Control.Handler = r => r.Cmd == "on"
            ? new ControlResponse(true, Status: rig.Control.Status = Samples.Bypass("running"))
            : new ControlResponse(true, Status: rig.Control.Status);

        await rig.Home.ConnectCommand.ExecuteAsync(null);

        Assert.Equal("включён · Авто", rig.Home.BypassLine);
        Assert.Null(rig.Home.Banner);
    }

    [Fact]
    public async Task Connect_WhenTheServiceRefusesToSwitchOn_SaysSo()
    {
        await using var rig = new HomeRig();
        rig.Control.Status = Samples.Bypass("disabled");
        rig.Control.Handler = r => r.Cmd == "on"
            ? new ControlResponse(false, "проверка параметров не прошла", Status: rig.Control.Status)
            : new ControlResponse(true, Status: rig.Control.Status);

        await rig.Home.ConnectCommand.ExecuteAsync(null);

        Assert.True(rig.Home.IsProxyRunning);   // the proxy is up anyway
        Assert.Equal("Обход DPI не включился: проверка параметров не прошла", rig.Home.Banner!.Text);
        Assert.Equal(Tone.Red, rig.Home.Banner.Tone);
    }

    [Fact]
    public async Task WhileStarting_TheButtonsAreOff_AndABusyIndicatorIsOn()
    {
        await using var rig = new HomeRig();
        var gate = new TaskCompletionSource();
        rig.Proxy.StartGate = gate.Task;

        Task connecting = rig.Home.ConnectCommand.ExecuteAsync(null);

        Assert.True(rig.Home.IsBusy);
        Assert.False(rig.Home.ConnectCommand.CanExecute(null));
        Assert.False(rig.Home.DisconnectCommand.CanExecute(null));
        Assert.False(rig.Home.NewSecretCommand.CanExecute(null));

        gate.SetResult();
        await connecting;

        Assert.False(rig.Home.IsBusy);
        Assert.True(rig.Home.ConnectCommand.CanExecute(null));
    }

    // ----- Отключить -----

    [Fact]
    public async Task Disconnect_StopsTheProxy_AndNeverTurnsTheSharedBypassOff()
    {
        await using var rig = new HomeRig();
        rig.Control.Status = Samples.Bypass("running");
        await rig.Home.ConnectCommand.ExecuteAsync(null);
        Assert.True(rig.Home.IsProxyRunning);

        await rig.Home.DisconnectCommand.ExecuteAsync(null);

        Assert.Equal(1, rig.Proxy.Stops);
        Assert.False(rig.Home.IsProxyRunning);
        Assert.Equal("Отключено", rig.Home.Headline);
        Assert.Equal(0, rig.Sent("off"));
        Assert.Equal("включён · Авто", rig.Home.BypassLine);   // still on for everyone
    }

    // ----- ссылка -----

    [Fact]
    public async Task OpenInTelegram_OpensTheLinkMadeFromTheCurrentSettings()
    {
        await using var rig = new HomeRig();
        rig.Settings.Link = "tg://proxy?server=127.0.0.1&port=1444&secret=ddabc";

        await rig.Home.OpenInTelegramCommand.ExecuteAsync(null);

        Assert.Equal(["tg://proxy?server=127.0.0.1&port=1444&secret=ddabc"], rig.Launcher.Opened);
        Assert.Null(rig.Home.Banner);
    }

    [Fact]
    public async Task OpenInTelegram_WhenNothingOpensIt_OffersToCopyTheLink()
    {
        await using var rig = new HomeRig();
        rig.Launcher.Succeeds = false;

        await rig.Home.OpenInTelegramCommand.ExecuteAsync(null);

        HomeBanner banner = Assert.IsType<HomeBanner>(rig.Home.Banner);
        Assert.Equal(BannerAction.CopyLink, banner.Action);
        Assert.Equal(Tone.Amber, banner.Tone);

        await rig.Home.RunBannerActionCommand.ExecuteAsync(null);

        Assert.Equal(rig.Settings.Link, rig.Clipboard.Text);
    }

    [Fact]
    public async Task CopyLink_PutsTheLinkOnTheClipboard()
    {
        await using var rig = new HomeRig();

        await rig.Home.CopyLinkCommand.ExecuteAsync(null);

        Assert.Equal(rig.Settings.Link, rig.Clipboard.Text);
        Assert.Equal("Ссылка скопирована", rig.Home.Notice);
    }

    // ----- новый секрет -----

    [Fact]
    public async Task NewSecret_Declined_ChangesNothing()
    {
        await using var rig = new HomeRig();
        rig.Dialogs.Answers.Enqueue(false);

        await rig.Home.NewSecretCommand.ExecuteAsync(null);

        Assert.Equal(0, rig.Settings.SecretsReplaced);
        Assert.Equal(["Новый секрет"], rig.Dialogs.Asked);
    }

    [Fact]
    public async Task NewSecret_WithNoProxyRunning_JustReplacesIt()
    {
        await using var rig = new HomeRig();
        rig.Proxy.RestartIsNeeded = false;

        await rig.Home.NewSecretCommand.ExecuteAsync(null);

        Assert.Equal(1, rig.Settings.SecretsReplaced);
        Assert.Equal(["Новый секрет"], rig.Dialogs.Asked);   // no restart to offer
        Assert.Equal("Создан новый секрет", rig.Home.Notice);
        Assert.Equal(0, rig.Proxy.Stops);
    }

    [Fact]
    public async Task NewSecret_WithARunningProxy_OffersTheRestart_AndDoesIt()
    {
        await using var rig = new HomeRig();
        await rig.Home.ConnectCommand.ExecuteAsync(null);
        rig.Proxy.RestartIsNeeded = true;   // what the real controller reports once the secret on disk differs

        await rig.Home.NewSecretCommand.ExecuteAsync(null);

        Assert.Equal(1, rig.Settings.SecretsReplaced);
        Assert.Equal(["Новый секрет", "Перезапустить прокси?"], rig.Dialogs.Asked);
        Assert.Equal(1, rig.Proxy.Stops);
        Assert.Equal(2, rig.Proxy.Starts);   // the first connect and the restart
        Assert.True(rig.Home.IsProxyRunning);
        Assert.Null(rig.Home.Banner);
    }

    [Fact]
    public async Task NewSecret_RestartLater_LeavesAReminderThatRestartsOnRequest()
    {
        await using var rig = new HomeRig();
        await rig.Home.ConnectCommand.ExecuteAsync(null);
        rig.Proxy.RestartIsNeeded = true;
        rig.Dialogs.Answers.Enqueue(true);    // create
        rig.Dialogs.Answers.Enqueue(false);   // restart: later

        await rig.Home.NewSecretCommand.ExecuteAsync(null);

        HomeBanner banner = Assert.IsType<HomeBanner>(rig.Home.Banner);
        Assert.Equal(BannerAction.Restart, banner.Action);
        Assert.Equal(0, rig.Proxy.Stops);

        await rig.Home.RunBannerActionCommand.ExecuteAsync(null);

        Assert.Equal(1, rig.Proxy.Stops);
        Assert.Equal(2, rig.Proxy.Starts);
        Assert.Null(rig.Home.Banner);
    }

    // ----- переходы -----

    [Fact]
    public async Task CheckConnection_GoesToDiagnostics_AndAsksForARun()
    {
        await using var rig = new HomeRig();

        rig.Home.CheckConnectionCommand.Execute(null);

        Assert.Equal([(AppPage.Diagnostics, true)], rig.Navigator.Shown);
    }

    [Fact]
    public async Task TheBypassRow_OpensTheBypassPage()
    {
        await using var rig = new HomeRig();

        rig.Home.OpenBypassCommand.Execute(null);

        Assert.Equal([(AppPage.Bypass, false)], rig.Navigator.Shown);
    }

    [Fact]
    public async Task DismissingTheBanner_ClearsIt()
    {
        await using var rig = new HomeRig();
        rig.Proxy.NextStart = new StartResult(StartOutcome.PortSwitched, 1444, OldPort: 1443);
        await rig.Home.ConnectCommand.ExecuteAsync(null);
        Assert.NotNull(rig.Home.Banner);

        rig.Home.DismissBannerCommand.Execute(null);

        Assert.Null(rig.Home.Banner);
    }
}
