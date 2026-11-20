using System.Text;
using TgwsOne.Core;
using TgwsOne.Core.Zapret;
using TgwsOne.Services;
using TgwsOne.ViewModels;

namespace TgwsOne.Tests;

/// <summary>Time that passes when the test says so.</summary>
internal sealed class ManualTicker : ITicker
{
    private Action? _tick;

    public bool Running => _tick != null;
    public TimeSpan? Period { get; private set; }
    public int Starts { get; private set; }

    public IDisposable Start(TimeSpan period, Action tick)
    {
        Starts++;
        Period = period;
        _tick = tick;
        return new Stop(this, tick);
    }

    /// <summary>One period passes (nothing happens if the ticker was stopped).</summary>
    public void Fire() => _tick?.Invoke();

    private sealed class Stop(ManualTicker owner, Action tick) : IDisposable
    {
        public void Dispose()
        {
            if (owner._tick == tick) owner._tick = null;
        }
    }
}

public class LogsViewModelTests
{
    private sealed class Rig : IDisposable
    {
        private readonly TempDir _tmp = new();

        public AppPaths App { get; }
        public ServicePaths Service { get; }
        public ManualTicker Ticker { get; } = new();
        public FakeClipboard Clipboard { get; } = new();
        public FakeLauncher Launcher { get; } = new();
        public LogsViewModel Vm { get; }

        public Rig()
        {
            App = AppPaths.For(OsKind.Linux, n => n == "TGWS_DATA_DIR" ? _tmp.Combine("data") : null, "/opt/tgwsone", "/home/nobody");
            Service = ServicePaths.For(OsKind.Linux, n => n == "TGWS_SERVICE_ROOT" ? _tmp.Combine("service") : null);
            Vm = new LogsViewModel(LogsViewModel.DefaultTabs(App, Service), Ticker, Clipboard, Launcher);
        }

        public string ServiceLog => Path.Combine(Service.LogsDir, "service.log");
        public string ZapretLog => Path.Combine(Service.LogsDir, "zapret.log");

        public static void Put(string path, string text)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.AppendAllText(path, text, new UTF8Encoding(false));
        }

        public void Dispose() => _tmp.Dispose();
    }

    [Fact]
    public void ThereAreThreeTabs_InTheSpecsOrder()
    {
        using var rig = new Rig();

        Assert.Equal(["Приложение", "Прокси", "Обход DPI"], rig.Vm.Tabs.Select(t => t.Title));
        Assert.Equal("Журналы", rig.Vm.Title);
        Assert.Same(rig.Vm.Tabs[0], rig.Vm.SelectedTab);
        Assert.True(rig.Vm.FollowEnd);
    }

    [Fact]
    public void TheTabsReadTheRightFiles()
    {
        using var rig = new Rig();
        Rig.Put(rig.App.AppLogFile, "из приложения\n");
        Rig.Put(rig.App.ProxyLogFile, "из прокси\n");

        rig.Vm.OnShown();
        Assert.Equal("из приложения", rig.Vm.Tabs[0].Text);

        rig.Vm.SelectedTab = rig.Vm.Tabs[1];
        Assert.Equal("из прокси", rig.Vm.Tabs[1].Text);
        Assert.Equal(Path.GetDirectoryName(rig.App.ProxyLogFile), rig.Vm.Tabs[1].Folder);
    }

    // ----- polling -----

    [Fact]
    public void WhileShown_TheSelectedTabIsPolledEverySecond()
    {
        using var rig = new Rig();
        Rig.Put(rig.App.AppLogFile, "раз\n");

        rig.Vm.OnShown();
        Assert.True(rig.Ticker.Running);
        Assert.Equal(TimeSpan.FromSeconds(1), rig.Ticker.Period);
        Assert.Equal("раз", rig.Vm.SelectedTab.Text);

        Rig.Put(rig.App.AppLogFile, "два\n");
        Assert.Equal("раз", rig.Vm.SelectedTab.Text);   // nothing happens between ticks
        rig.Ticker.Fire();

        Assert.Equal("раз\nдва", rig.Vm.SelectedTab.Text);
    }

    [Fact]
    public void WhenHidden_ThePollingStops_AndComesBackWhenShownAgain()
    {
        using var rig = new Rig();
        rig.Vm.OnShown();
        Assert.True(rig.Ticker.Running);

        rig.Vm.OnHidden();
        Assert.False(rig.Ticker.Running);
        Rig.Put(rig.App.AppLogFile, "пока вас не было\n");
        rig.Ticker.Fire();
        Assert.Equal("", rig.Vm.SelectedTab.Text);   // no tick was delivered

        rig.Vm.OnShown();
        Assert.True(rig.Ticker.Running);
        Assert.Equal("пока вас не было", rig.Vm.SelectedTab.Text);   // and it catches up at once
        Assert.Equal(2, rig.Ticker.Starts);
    }

    [Fact]
    public void ShowingTheSamePageTwice_DoesNotStackTimers()
    {
        using var rig = new Rig();

        rig.Vm.OnShown();
        rig.Vm.OnShown();
        rig.Vm.OnHidden();

        Assert.False(rig.Ticker.Running);   // the first timer is not left running behind the second
    }

    [Fact]
    public void ChoosingATab_ReadsItAtOnce_AndPollsThatOne()
    {
        using var rig = new Rig();
        Rig.Put(rig.App.ProxyLogFile, "строка прокси\n");
        rig.Vm.OnShown();

        rig.Vm.SelectedTab = rig.Vm.Tabs[1];
        Assert.Equal("строка прокси", rig.Vm.SelectedTab.Text);

        Rig.Put(rig.App.ProxyLogFile, "ещё одна\n");
        rig.Ticker.Fire();
        Assert.Equal("строка прокси\nещё одна", rig.Vm.Tabs[1].Text);
    }

    [Fact]
    public void ThePageHooks_AreCalledByTheWindow()
    {
        using var rig = new Rig();
        var window = new MainWindowViewModel(_ =>
        [
            MainWindowViewModel.Placeholder(AppPage.Home), MainWindowViewModel.Placeholder(AppPage.Bypass),
            MainWindowViewModel.Placeholder(AppPage.Diagnostics), rig.Vm, MainWindowViewModel.Placeholder(AppPage.Settings),
        ]);

        window.Show(AppPage.Logs);
        Assert.True(rig.Ticker.Running);

        window.Show(AppPage.Home);
        Assert.False(rig.Ticker.Running);
    }

    // ----- the service tab -----

    [Fact]
    public void TheServiceTab_ShowsBothFiles_EachUnderItsName()
    {
        using var rig = new Rig();
        Rig.Put(rig.ServiceLog, "служба запущена\n");
        Rig.Put(rig.ZapretLog, "--- запуск nfqws2\nfake zapret running\n");
        rig.Vm.SelectedTab = rig.Vm.Tabs[2];

        Assert.Equal("=== service.log ===\nслужба запущена\n\n=== zapret.log ===\n--- запуск nfqws2\nfake zapret running", rig.Vm.SelectedTab.Text);
        Assert.False(rig.Vm.SelectedTab.HasProblem);
        Assert.Equal(rig.Service.LogsDir, rig.Vm.SelectedTab.Folder);
    }

    [Fact]
    public void TheServiceTab_WithOneFileMissing_StillShowsTheOther()
    {
        using var rig = new Rig();
        Rig.Put(rig.ServiceLog, "служба запущена\n");
        rig.Vm.SelectedTab = rig.Vm.Tabs[2];

        Assert.Contains("служба запущена", rig.Vm.SelectedTab.Text);
        Assert.Contains("=== zapret.log ===\nЖурнала пока нет", rig.Vm.SelectedTab.Text);
        Assert.False(rig.Vm.SelectedTab.HasProblem);
    }

    [Fact]
    public void AnEmptyFile_InTheServiceTab_SaysItIsEmpty()
    {
        using var rig = new Rig();
        Rig.Put(rig.ServiceLog, "");
        Rig.Put(rig.ZapretLog, "");
        rig.Vm.SelectedTab = rig.Vm.Tabs[2];

        Assert.Equal("=== service.log ===\n(пусто)\n\n=== zapret.log ===\n(пусто)", rig.Vm.SelectedTab.Text);
    }

    [Fact]
    public void WithNoFileAtAll_ThereIsAMessageInsteadOfText()
    {
        using var rig = new Rig();

        rig.Vm.OnShown();

        Assert.True(rig.Vm.SelectedTab.HasProblem);
        Assert.Contains("Журнала пока нет", rig.Vm.SelectedTab.Problem);
        Assert.Equal("", rig.Vm.SelectedTab.Text);
    }

    [Fact]
    public void TheMessageGoes_WhenTheFileAppears()
    {
        using var rig = new Rig();
        rig.Vm.OnShown();
        Assert.True(rig.Vm.SelectedTab.HasProblem);

        Rig.Put(rig.App.AppLogFile, "наконец-то\n");
        rig.Ticker.Fire();

        Assert.False(rig.Vm.SelectedTab.HasProblem);
        Assert.Equal("наконец-то", rig.Vm.SelectedTab.Text);
    }

    // ----- buttons -----

    [Fact]
    public async Task Copy_PutsTheShownTextOnTheClipboard()
    {
        using var rig = new Rig();
        Rig.Put(rig.App.AppLogFile, "одна\nдругая\n");
        rig.Vm.OnShown();

        await rig.Vm.CopyCommand.ExecuteAsync(null);

        Assert.Equal("одна\nдругая", rig.Clipboard.Text);
        Assert.Equal("Скопировано", rig.Vm.Notice);
    }

    [Fact]
    public async Task OpenFolder_OpensTheFolderOfTheTab()
    {
        using var rig = new Rig();
        rig.Vm.SelectedTab = rig.Vm.Tabs[2];

        await rig.Vm.OpenFolderCommand.ExecuteAsync(null);

        Assert.Equal([rig.Service.LogsDir], rig.Launcher.Opened);
        Assert.Null(rig.Vm.Notice);
    }

    [Fact]
    public async Task OpenFolder_ThatCannotBeOpened_SaysSo()
    {
        using var rig = new Rig();
        rig.Launcher.Succeeds = false;

        await rig.Vm.OpenFolderCommand.ExecuteAsync(null);

        Assert.StartsWith("Не удалось открыть папку:", rig.Vm.Notice);
    }

    [Fact]
    public void TheFollowSwitch_ReachesEveryTab()
    {
        using var rig = new Rig();

        rig.Vm.FollowEnd = false;

        Assert.All(rig.Vm.Tabs, t => Assert.False(t.FollowEnd));

        rig.Vm.FollowEnd = true;
        Assert.All(rig.Vm.Tabs, t => Assert.True(t.FollowEnd));
    }
}
