using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using TgwsOne.Engine;
using TgwsOne.Mobile.Views;

namespace TgwsOne.Mobile.Tests;

/// <summary>One window, a menu and the system's back button: the first launch starts on the Telegram page.</summary>
public class MobileShellViewTests
{
    private sealed class Rig : IDisposable
    {
        private readonly TempFiles _files = new();
        public int FirstRunDone { get; private set; }

        public HomeViewModel Home { get; }
        public BypassViewModel Bypass { get; }
        public FinderViewModel Finder { get; }
        public DiagnosticsViewModel Diagnostics { get; }
        public LogsViewModel Logs { get; }
        public SettingsViewModel Settings { get; }
        public ConnectViewModel Connect { get; }
        public MobileShellViewModel Shell { get; }

        public Rig(bool firstRun)
        {
            var engine = new FakeEngineControl
            {
                Status = new EngineProxyStatus(false, false, 0, PortBusyByOther: false, Pid: null),
            };
            var settings = new FakeMobileSettings();
            var control = new FakeBypassControl();
            var finderControl = new FakeDesyncControl();

            Home = new HomeViewModel(engine, settings, () => 0);
            Bypass = new BypassViewModel(control, settings);
            Finder = new FinderViewModel(new DesyncFinder(finderControl, new FinderProbes(finderControl),
                                                          (_, _) => Task.CompletedTask));
            Diagnostics = new DiagnosticsViewModel(
                new Diagnostics(engine, control, new DiagnosticProbes(), () => true, () => 0),
                new FakeDiagnosticsActions());
            Logs = new LogsViewModel(LogsViewModel.DefaultTabs(_files.Storage), new FakeTicker(),
                                     new FakeClipboard(), new FakeLauncher());
            Settings = new SettingsViewModel(settings);
            Connect = new ConnectViewModel(settings, new FakeLauncher(), new FakeClipboard(), new FakeShareService());
            Shell = new MobileShellViewModel(Home, Bypass, Finder, Diagnostics, Logs, Settings, Connect,
                                             firstRun, () => FirstRunDone++);
        }

        public void Dispose() => _files.Dispose();
    }

    private static T Find<T>(Control view, string name) where T : Control
        => view.FindControl<T>(name) ?? throw new InvalidOperationException($"no control named {name}");

    private static MobileShellView Show(Rig rig)
    {
        var view = new MobileShellView { DataContext = rig.Shell };
        new Window { Content = view, Width = 420, Height = 800 }.Show();
        Dispatcher.UIThread.RunJobs();
        return view;
    }

    [AvaloniaFact]
    public void FirstRun_StartsOnConnect_AndContinueGoesHome()
    {
        using var rig = new Rig(firstRun: true);
        MobileShellView view = Show(rig);

        Assert.IsType<ConnectViewModel>(Find<ContentControl>(view, "PageHost").Content);
        Assert.Equal("Подключение к Telegram", Find<TextBlock>(view, "TitleText").Text);
        Assert.True(Find<Button>(view, "ContinueButton").IsVisible);

        rig.Shell.ContinueCommand.Execute(null);
        Dispatcher.UIThread.RunJobs();

        Assert.IsType<HomeViewModel>(Find<ContentControl>(view, "PageHost").Content);
        Assert.Equal("Главная", Find<TextBlock>(view, "TitleText").Text);
        Assert.Equal(1, rig.FirstRunDone);
        Assert.False(Find<Button>(view, "ContinueButton").IsEffectivelyVisible);
    }

    [AvaloniaFact]
    public void TheMenuOpensEveryPage_AndBackReturnsToWhereItWas()
    {
        using var rig = new Rig(firstRun: false);
        MobileShellView view = Show(rig);

        (string Page, Type ViewModel)[] pages =
        [
            ("Connect", typeof(ConnectViewModel)),
            ("Bypass", typeof(BypassViewModel)),
            ("Finder", typeof(FinderViewModel)),
            ("Diagnostics", typeof(DiagnosticsViewModel)),
            ("Logs", typeof(LogsViewModel)),
            ("Settings", typeof(SettingsViewModel)),
            ("Home", typeof(HomeViewModel)),
        ];

        foreach ((string page, Type vm) in pages)
        {
            rig.Shell.NavigateCommand.Execute(page);
            Dispatcher.UIThread.RunJobs();
            Assert.IsType(vm, Find<ContentControl>(view, "PageHost").Content);
            Assert.True(Find<Button>(view, "BackButton").IsVisible);
        }

        Assert.True(rig.Shell.GoBack());
        Dispatcher.UIThread.RunJobs();
        Assert.IsType<SettingsViewModel>(Find<ContentControl>(view, "PageHost").Content);
    }

    [AvaloniaFact]
    public void AtTheFirstPage_BackIsLeftToTheSystem()
    {
        using var rig = new Rig(firstRun: false);
        Show(rig);

        Assert.False(rig.Shell.GoBack());
        Assert.False(rig.Shell.CanGoBack);
    }
}
