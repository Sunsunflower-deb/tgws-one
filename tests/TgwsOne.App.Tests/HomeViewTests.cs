using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using TgwsOne.Core;
using TgwsOne.Views;

namespace TgwsOne.Tests;

/// <summary>Главная drawn on the headless platform: what is on the screen follows the view-model.</summary>
public class HomeViewTests
{
    private static HomeView Show(HomeRig rig)
    {
        var view = new HomeView { DataContext = rig.Home };
        new Window { Content = view, Width = 820, Height = 540 }.Show();
        Dispatcher.UIThread.RunJobs();
        return view;
    }

    private static T Find<T>(HomeView view, string name) where T : Control
        => view.FindControl<T>(name) ?? throw new InvalidOperationException($"no control named {name}");

    [AvaloniaFact]
    public async Task Stopped_ShowsConnect_NotDisconnect()
    {
        await using var rig = new HomeRig();
        await rig.Monitor.RefreshNowAsync();
        HomeView view = Show(rig);

        Assert.True(Find<Button>(view, "ConnectButton").IsVisible);
        Assert.Equal("Подключить", Find<Button>(view, "ConnectButton").Content);
        Assert.False(Find<Button>(view, "DisconnectButton").IsVisible);
        Assert.False(Find<Button>(view, "TelegramButton").IsVisible);
        Assert.Equal("Отключено", Find<TextBlock>(view, "HeadlineText").Text);
        Assert.Equal("остановлен", Find<TextBlock>(view, "ProxyText").Text);
        Assert.False(Find<Border>(view, "BannerPanel").IsVisible);
    }

    [AvaloniaFact]
    public async Task Running_ShowsDisconnectAndTelegram_NotConnect()
    {
        await using var rig = new HomeRig();
        HomeView view = Show(rig);

        await rig.Home.ConnectCommand.ExecuteAsync(null);
        Dispatcher.UIThread.RunJobs();

        Assert.False(Find<Button>(view, "ConnectButton").IsVisible);
        Assert.True(Find<Button>(view, "DisconnectButton").IsVisible);
        Assert.Equal("Отключить", Find<Button>(view, "DisconnectButton").Content);
        Assert.True(Find<Button>(view, "TelegramButton").IsVisible);
        Assert.Equal("работает · 127.0.0.1:1443", Find<TextBlock>(view, "ProxyText").Text);
    }

    [AvaloniaFact]
    public async Task Disconnect_BringsConnectBack()
    {
        await using var rig = new HomeRig();
        HomeView view = Show(rig);
        await rig.Home.ConnectCommand.ExecuteAsync(null);

        await rig.Home.DisconnectCommand.ExecuteAsync(null);
        Dispatcher.UIThread.RunJobs();

        Assert.True(Find<Button>(view, "ConnectButton").IsVisible);
        Assert.False(Find<Button>(view, "DisconnectButton").IsVisible);
    }

    [AvaloniaFact]
    public async Task APortSwitch_ShowsTheBannerWithItsButton()
    {
        await using var rig = new HomeRig();
        rig.Proxy.NextStart = new StartResult(StartOutcome.PortSwitched, 1444, OldPort: 1443);
        HomeView view = Show(rig);

        await rig.Home.ConnectCommand.ExecuteAsync(null);
        Dispatcher.UIThread.RunJobs();

        Assert.True(Find<Border>(view, "BannerPanel").IsVisible);
        Assert.Equal("Порт 1443 занят — прокси на 1444. Откройте ссылку в Telegram заново.", Find<TextBlock>(view, "BannerText").Text);
        Assert.True(Find<Button>(view, "BannerActionButton").IsVisible);
        Assert.Equal("Открыть в Telegram", Find<Button>(view, "BannerActionButton").Content);
    }

    [AvaloniaFact]
    public async Task ABannerWithoutAnAction_HasNoActionButton()
    {
        await using var rig = new HomeRig();
        rig.Proxy.NextStart = new StartResult(StartOutcome.Timeout, 1443);
        HomeView view = Show(rig);

        await rig.Home.ConnectCommand.ExecuteAsync(null);
        Dispatcher.UIThread.RunJobs();

        Assert.True(Find<Border>(view, "BannerPanel").IsVisible);
        Assert.False(Find<Button>(view, "BannerActionButton").IsVisible);
    }

    [AvaloniaFact]
    public async Task TheBypassRow_IsAlwaysClickable_ButOnlyPointsOnWhenThereIsAService()
    {
        // The page it opens is where a missing service is explained, so the row never looks or acts disabled.
        await using var rig = new HomeRig();
        HomeView view = Show(rig);
        await rig.Monitor.RefreshNowAsync();
        Dispatcher.UIThread.RunJobs();
        Assert.True(Find<Button>(view, "BypassRow").IsEnabled);
        Assert.True(Find<Icon>(view, "BypassChevron").IsVisible);

        rig.Control.Unavailable = true;
        await rig.Monitor.RefreshNowAsync();
        Dispatcher.UIThread.RunJobs();

        Assert.True(Find<Button>(view, "BypassRow").IsEnabled);
        Assert.False(Find<Icon>(view, "BypassChevron").IsVisible);
        Assert.Equal("не установлен — нужен администратор", Find<TextBlock>(view, "BypassText").Text);
    }

    [AvaloniaFact]
    public async Task TheButtons_AreWiredToTheCommands()
    {
        await using var rig = new HomeRig();
        HomeView view = Show(rig);

        Find<Button>(view, "CheckButton").Command!.Execute(null);

        Assert.Equal([(TgwsOne.ViewModels.AppPage.Diagnostics, true)], rig.Navigator.Shown);
    }
}
