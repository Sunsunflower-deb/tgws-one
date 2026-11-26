namespace TgwsOne.Mobile.Tests;

public class LogsViewModelTests
{
    private static void Write(string path, string text)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, text);
    }

    private sealed class Rig : IDisposable
    {
        public TempFiles Files { get; } = new();
        public FakeTicker Ticker { get; } = new();
        public FakeClipboard Clipboard { get; } = new();
        public FakeLauncher Launcher { get; } = new();
        public LogsViewModel Vm { get; }

        public Rig()
        {
            Vm = new LogsViewModel(LogsViewModel.DefaultTabs(Files.Storage), Ticker, Clipboard, Launcher);
        }

        public void WriteApp(string text) => Write(Files.Storage.AppLogPath, text);

        public void Dispose() => Files.Dispose();
    }

    [Fact]
    public void ThereAreTwoTabs_TheApplicationAndTheProxy()
    {
        using var files = new TempFiles();

        IReadOnlyList<LogTabViewModel> tabs = LogsViewModel.DefaultTabs(files.Storage);

        Assert.Equal(["Приложение", "Прокси"], tabs.Select(tab => tab.Title));
        Assert.Equal([files.Storage.AppLogPath, files.Storage.ProxyLogPath], tabs.Select(tab => tab.Source.Path));
    }

    [Fact]
    public void ALogThatIsNotThereYet_IsAMessage_NotAnError()
    {
        using var files = new TempFiles();
        LogTabViewModel tab = LogsViewModel.DefaultTabs(files.Storage)[0];

        Assert.True(tab.Refresh());

        Assert.Equal("", tab.Text);
        Assert.True(tab.HasProblem);
        Assert.Equal("Журнала пока нет: он появится, когда будет что записать.", tab.Problem);
    }

    [Fact]
    public void Refresh_ShowsTheNewestLines()
    {
        using var rig = new Rig();
        rig.WriteApp("первая\nвторая\nтретья");

        Assert.True(rig.Vm.SelectedTab.Refresh());

        Assert.Equal("первая\nвторая\nтретья", rig.Vm.SelectedTab.Text);
        Assert.False(rig.Vm.SelectedTab.HasProblem);
    }

    [Fact]
    public void ACleanRefresh_ReportsNoChange()
    {
        using var rig = new Rig();
        rig.WriteApp("одна строка");
        rig.Vm.SelectedTab.Refresh();

        Assert.False(rig.Vm.SelectedTab.Refresh());
    }

    [Fact]
    public void OnShown_PollsTheChosenTab_AndOnHiddenStops()
    {
        using var rig = new Rig();
        rig.WriteApp("старое");

        rig.Vm.OnShown();
        Assert.Equal(1, rig.Ticker.Started);
        Assert.Equal(TimeSpan.FromSeconds(1), rig.Ticker.Period);

        rig.WriteApp("старое\nновое");
        rig.Ticker.Tick();
        Assert.Contains("новое", rig.Vm.SelectedTab.Text);

        rig.Vm.OnHidden();
        Assert.Equal(1, rig.Ticker.Stopped);
        Assert.Equal(1, rig.Ticker.Started);
    }

    [Fact]
    public void ChoosingAnotherTab_ReadsItAtOnce()
    {
        using var rig = new Rig();
        Write(rig.Files.Storage.ProxyLogPath, "журнал прокси");

        rig.Vm.SelectedTab = rig.Vm.Tabs[1];

        Assert.Equal("журнал прокси", rig.Vm.SelectedTab.Text);
    }

    [Fact]
    public void FollowEnd_AppliesToEveryTab()
    {
        using var rig = new Rig();

        rig.Vm.FollowEnd = false;

        Assert.All(rig.Vm.Tabs, tab => Assert.False(tab.FollowEnd));
    }

    [Fact]
    public async Task Copy_PutsTheShownTextOnTheClipboard()
    {
        using var rig = new Rig();
        rig.WriteApp("строка журнала");
        rig.Vm.SelectedTab.Refresh();

        await rig.Vm.CopyCommand.ExecuteAsync(null);

        Assert.Equal("строка журнала", rig.Clipboard.Text);
        Assert.Equal("Скопировано", rig.Vm.Notice);
    }

    [Fact]
    public async Task OpenFolder_OpensWhereTheLogLives()
    {
        using var rig = new Rig();
        rig.WriteApp("x");
        rig.Vm.SelectedTab.Refresh();

        await rig.Vm.OpenFolderCommand.ExecuteAsync(null);

        Assert.Equal([Path.GetDirectoryName(rig.Files.Storage.AppLogPath)!], rig.Launcher.Opened);
    }

    [Fact]
    public async Task OpenFolder_WhenItCannot_FallsBackToAMessage()
    {
        using var rig = new Rig();
        rig.Launcher.Succeeds = false;

        await rig.Vm.OpenFolderCommand.ExecuteAsync(null);

        Assert.StartsWith("Не удалось открыть папку:", rig.Vm.Notice);
    }

    [Fact]
    public void TheRefreshCommand_ReadsTheFileAgain()
    {
        using var rig = new Rig();
        rig.WriteApp("до");
        rig.Vm.SelectedTab.Refresh();
        rig.WriteApp("после");

        rig.Vm.RefreshCommand.Execute(null);

        Assert.Equal("после", rig.Vm.SelectedTab.Text);
    }
}
