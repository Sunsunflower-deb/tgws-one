using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace TgwsOne.Mobile;

/// <summary>One log file that goes into a tab.</summary>
public sealed record LogSource(string Name, string Path);

/// <summary>One tab of Журналы: the newest lines of one file, or a message saying why there are none.</summary>
public sealed partial class LogTabViewModel : ObservableObject
{
    private const int MaxLines = 500;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasProblem))]
    private string _text = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasProblem))]
    private string? _problem;

    /// <summary>Set by the page: keep the newest line in view.</summary>
    [ObservableProperty]
    private bool _followEnd = true;

    public LogTabViewModel(string title, LogSource source)
    {
        Title = title;
        Source = source;
        Folder = Path.GetDirectoryName(source.Path) ?? "";
    }

    public string Title { get; }
    public LogSource Source { get; }
    public string Folder { get; }
    public bool HasProblem => Problem != null;

    /// <summary>Looks at the file again. True when the tab's text changed.</summary>
    public bool Refresh()
    {
        string? problem = null;
        string text;
        try
        {
            text = string.Join('\n', File.ReadLines(Source.Path).TakeLast(MaxLines));
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
        {
            text = "";
            problem = "Журнала пока нет: он появится, когда будет что записать.";
        }
        catch (UnauthorizedAccessException)
        {
            text = "";
            problem = "Нет доступа к этому журналу.";
        }
        catch (IOException ex)
        {
            text = "";
            problem = $"Не удалось прочитать журнал: {ex.Message}";
        }

        bool changed = text != Text || problem != Problem;
        Text = text;
        Problem = problem;
        return changed;
    }
}

/// <summary>
/// Журналы on the phone: the application's and the proxy's logs, kept up to date while the page is open. There is no
/// bypass service here, so there are two tabs rather than the desktop's three.
/// </summary>
public sealed partial class LogsViewModel : ObservableObject
{
    public static readonly TimeSpan PollEvery = TimeSpan.FromSeconds(1);

    private readonly ITicker _ticker;
    private readonly IClipboardService _clipboard;
    private readonly ILauncher _launcher;
    private IDisposable? _polling;

    [ObservableProperty] private LogTabViewModel _selectedTab;
    [ObservableProperty] private bool _followEnd = true;
    [ObservableProperty] private string? _notice;

    public LogsViewModel(IReadOnlyList<LogTabViewModel> tabs, ITicker ticker, IClipboardService clipboard, ILauncher launcher)
    {
        ArgumentOutOfRangeException.ThrowIfZero(tabs.Count);
        _ticker = ticker;
        _clipboard = clipboard;
        _launcher = launcher;
        Tabs = new ObservableCollection<LogTabViewModel>(tabs);
        _selectedTab = tabs[0];
    }

    public ObservableCollection<LogTabViewModel> Tabs { get; }

    /// <summary>The two tabs this build has: the application's and the engine's logs.</summary>
    public static IReadOnlyList<LogTabViewModel> DefaultTabs(MobileStorage storage) =>
    [
        new LogTabViewModel("Приложение", new LogSource("app.log", storage.AppLogPath)),
        new LogTabViewModel("Прокси", new LogSource("proxy.log", storage.ProxyLogPath)),
    ];

    partial void OnSelectedTabChanged(LogTabViewModel value) => value.Refresh();

    partial void OnFollowEndChanged(bool value)
    {
        foreach (LogTabViewModel tab in Tabs) tab.FollowEnd = value;
    }

    public void OnShown()
    {
        SelectedTab.Refresh();
        _polling?.Dispose();
        _polling = _ticker.Start(PollEvery, () => SelectedTab.Refresh());
    }

    public void OnHidden()
    {
        _polling?.Dispose();
        _polling = null;
    }

    [RelayCommand]
    private void Refresh() => SelectedTab.Refresh();

    [RelayCommand]
    private async Task CopyAsync()
    {
        await _clipboard.SetTextAsync(SelectedTab.Text);
        Notice = "Скопировано";
    }

    [RelayCommand]
    private async Task OpenFolderAsync()
    {
        Notice = null;
        if (!await _launcher.OpenAsync(SelectedTab.Folder))
            Notice = $"Не удалось открыть папку: {SelectedTab.Folder}";
    }
}
