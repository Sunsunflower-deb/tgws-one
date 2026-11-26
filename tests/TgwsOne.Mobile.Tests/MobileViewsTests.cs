using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using TgwsOne.Engine;
using TgwsOne.Mobile.Views;

namespace TgwsOne.Mobile.Tests;

/// <summary>The phone pages on the headless platform: each screen is opened and its main controls are checked.</summary>
public class MobileViewsTests
{
    private static T Find<T>(Control view, string name) where T : Control
        => view.FindControl<T>(name) ?? throw new InvalidOperationException($"no control named {name}");

    private static void Show(Control view, double height = 800)
    {
        new Window { Content = view, Width = 420, Height = height }.Show();
        Dispatcher.UIThread.RunJobs();
    }

    [AvaloniaFact]
    public async Task Home_StartsAndStopsFromItsOneButton()
    {
        var engine = new FakeEngineControl
        {
            Status = new EngineProxyStatus(false, false, 0, PortBusyByOther: false, Pid: null),
        };
        var vm = new HomeViewModel(engine, new FakeMobileSettings(), () => 0);
        var view = new HomeView { DataContext = vm };
        Show(view);

        Assert.Equal("Отключено", Find<TextBlock>(view, "HeadlineText").Text);
        Assert.Equal("остановлен", Find<TextBlock>(view, "ProxyText").Text);
        Assert.Equal("Запустить", Find<Button>(view, "ToggleButton").Content);
        Assert.False(Find<Border>(view, "BannerPanel").IsVisible);

        await vm.ToggleCommand.ExecuteAsync(null);
        Dispatcher.UIThread.RunJobs();

        Assert.Equal("Остановить", Find<Button>(view, "ToggleButton").Content);
        Assert.Equal("работает · 127.0.0.1:1443", Find<TextBlock>(view, "ProxyText").Text);
    }

    [AvaloniaFact]
    public async Task Connect_ShowsTheLinkAndItsThreeActions()
    {
        var settings = new FakeMobileSettings();
        var launcher = new FakeLauncher();
        var clipboard = new FakeClipboard();
        var share = new FakeShareService();
        var vm = new ConnectViewModel(settings, launcher, clipboard, share);
        var view = new ConnectView { DataContext = vm };
        Show(view);

        Assert.Equal(settings.Link, Find<SelectableTextBlock>(view, "LinkText").Text);
        Assert.Equal("Открыть в Telegram", Find<Button>(view, "OpenTelegramButton").Content);
        Assert.Equal("Копировать", Find<Button>(view, "CopyButton").Content);
        Assert.Equal("Поделиться", Find<Button>(view, "ShareButton").Content);

        await vm.OpenInTelegramCommand.ExecuteAsync(null);
        Assert.Equal([settings.Link], launcher.Opened);

        await vm.ShareLinkCommand.ExecuteAsync(null);
        Assert.Equal([settings.Link], share.Shared);
    }

    [AvaloniaFact]
    public async Task Bypass_ShowsTheSwitchTheStrategiesAndTheAutoTable()
    {
        var control = new FakeBypassControl();
        control.AutoHosts = [new TgwsOne.Engine.Desync.AutoHost("kws2.web.telegram.org", "split-sni", 0)];
        var vm = new BypassViewModel(control, new FakeMobileSettings());
        await vm.LoadAsync();
        var view = new BypassView { DataContext = vm };
        Show(view);

        Assert.True(Find<ToggleSwitch>(view, "BypassSwitch").IsChecked);
        Assert.Equal("включён · Авто · kws2 → Разрез после имени", Find<TextBlock>(view, "StatusLineText").Text);
        Assert.True(Find<Border>(view, "AutoPanel").IsVisible);
        Assert.False(Find<TextBlock>(view, "AutoEmptyText").IsVisible);
        Assert.True(Find<ItemsControl>(view, "StrategyList").ItemCount > 1);
    }

    [AvaloniaFact]
    public void Finder_StartsWithItsExplanation()
    {
        var control = new FakeDesyncControl();
        var vm = new FinderViewModel(new DesyncFinder(control, new FinderProbes(control), (_, _) => Task.CompletedTask));
        var view = new FinderView { DataContext = vm };
        Show(view);

        Assert.True(Find<StackPanel>(view, "ConfirmPanel").IsVisible);
        Assert.False(Find<StackPanel>(view, "ResultPanel").IsVisible);
        Assert.Equal(FinderViewModel.ConfirmText, Find<TextBlock>(view, "ConfirmText").Text);
        Assert.Equal("Начать подбор", Find<Button>(view, "StartButton").Content);
    }

    [AvaloniaFact]
    public async Task Diagnostics_RunsAndShowsItsChecksAndVerdict()
    {
        var engine = new FakeEngineControl();
        var vm = new DiagnosticsViewModel(
            new Diagnostics(engine, new FakeBypassControl(), new DiagnosticProbes(), () => true, () => 1),
            new FakeDiagnosticsActions());
        var view = new DiagnosticsView { DataContext = vm };
        Show(view);

        Assert.Equal("Запустить", Find<Button>(view, "RunButton").Content);
        Assert.True(Find<TextBlock>(view, "HintText").IsVisible);

        await vm.RunCommand.ExecuteAsync(null);
        Dispatcher.UIThread.RunJobs();

        Assert.True(Find<Border>(view, "ChecksPanel").IsVisible);
        Assert.Equal(8, Find<ItemsControl>(view, "CheckList").ItemCount);
        Assert.Equal("Всё в порядке.", Find<TextBlock>(view, "VerdictText").Text);
        Assert.False(Find<Button>(view, "ActionButton").IsVisible);
    }

    [AvaloniaFact]
    public void Logs_ShowsBothJournalsAndTheirButtons()
    {
        using var files = new TempFiles();
        Directory.CreateDirectory(Path.GetDirectoryName(files.Storage.AppLogPath)!);
        File.WriteAllText(files.Storage.AppLogPath, "приложение запущено\n");
        var vm = new LogsViewModel(LogsViewModel.DefaultTabs(files.Storage), new FakeTicker(),
                                   new FakeClipboard(), new FakeLauncher());
        vm.OnShown();
        var view = new LogsView { DataContext = vm };
        Show(view);

        Assert.Equal(2, Find<TabControl>(view, "TabsControl").ItemCount);
        Assert.Equal("Копировать", Find<Button>(view, "CopyButton").Content);
        Assert.Equal("Открыть папку", Find<Button>(view, "OpenFolderButton").Content);
        Assert.True(Find<CheckBox>(view, "FollowCheck").IsChecked);
    }

    [AvaloniaFact]
    public void Settings_ShowsTheSavedValuesAndSaves()
    {
        var settings = new FakeMobileSettings { Android = new AndroidSettings("split-sni", "mts", 60) };
        var vm = new SettingsViewModel(settings);
        var view = new SettingsView { DataContext = vm };
        Show(view);

        Assert.Equal("split-sni", ((StrategyChoice)Find<ComboBox>(view, "StrategyBox").SelectedItem!).Id);
        Assert.Equal("mts", Find<TextBox>(view, "ProviderBox").Text);
        Assert.Equal("60", Find<TextBox>(view, "PoolIdleBox").Text);

        Find<TextBox>(view, "ProviderBox").Text = "beeline";
        Find<Button>(view, "SaveButton").Command!.Execute(null);
        Dispatcher.UIThread.RunJobs();

        Assert.Equal("beeline", settings.Android.Provider);
        Assert.Equal("Сохранено", Find<TextBlock>(view, "NoticeText").Text);
    }
}
