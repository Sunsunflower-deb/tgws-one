using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using TgwsOne.Core;
using TgwsOne.Core.Zapret;
using TgwsOne.Services;

namespace TgwsOne.ViewModels;

/// <summary>One log file that goes into a tab.</summary>
internal sealed record LogSource(string Name, string Path);

/// <summary>One tab of Журналы: one file, or several shown one after another under their names.</summary>
internal sealed partial class LogTabViewModel : ObservableObject
{
    private readonly (string Name, LogTail Tail)[] _tails;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasProblem))]
    private string _text = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasProblem))]
    private string? _problem;

    /// <summary>Set by the page (one switch for all tabs): keep the newest line in view.</summary>
    [ObservableProperty]
    private bool _followEnd = true;

    public LogTabViewModel(string title, IReadOnlyList<LogSource> sources, int maxLines = 500)
    {
        Title = title;
        _tails = [.. sources.Select(s => (s.Name, new LogTail(s.Path, maxLines)))];
        Folder = Path.GetDirectoryName(sources[0].Path) ?? "";
    }

    public string Title { get; }

    /// <summary>Where the files are: what «Открыть папку» opens.</summary>
    public string Folder { get; }

    /// <summary>There is nothing to show but a message (no file yet, or no access to it).</summary>
    public bool HasProblem => Problem != null;

    /// <summary>Looks at the files again.</summary>
    /// <returns>True when the tab's text changed.</returns>
    public bool Refresh()
    {
        bool changed = false;
        foreach ((string _, LogTail tail) in _tails) changed |= tail.Refresh();

        Problem = _tails.All(t => t.Tail.Problem != null) ? _tails[0].Tail.Problem : null;
        string text = _tails.Length == 1 ? string.Join('\n', _tails[0].Tail.Lines) : Sections();
        if (text == Text) return changed;

        Text = text;
        return true;
    }

    // Several files: each under its own name, ASCII only (a drawn rule would need a glyph the font may not have).
    private string Sections() => string.Join("\n\n", _tails.Select(t =>
        $"=== {t.Name} ===\n" + (t.Tail.Problem ?? (t.Tail.Lines.Count == 0 ? "(пусто)" : string.Join('\n', t.Tail.Lines)))));
}

/// <summary>Журналы: the application's, the proxy's and the bypass service's logs, kept up to date while the page is open.</summary>
internal sealed partial class LogsViewModel : PageViewModel
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
        : base(AppPageInfo.Title(AppPage.Logs), AppPageInfo.Icon(AppPage.Logs))
    {
        ArgumentOutOfRangeException.ThrowIfZero(tabs.Count);
        _ticker = ticker;
        _clipboard = clipboard;
        _launcher = launcher;
        Tabs = new ObservableCollection<LogTabViewModel>(tabs);
        _selectedTab = tabs[0];
    }

    public ObservableCollection<LogTabViewModel> Tabs { get; }

    /// <summary>The usual three tabs for this machine: the app's, the proxy's and the bypass service's (read-only: its files belong to the service).</summary>
    public static IReadOnlyList<LogTabViewModel> DefaultTabs(AppPaths app, ServicePaths service) =>
    [
        new LogTabViewModel("Приложение", [new LogSource("app.log", app.AppLogFile)]),
        new LogTabViewModel("Прокси", [new LogSource("proxy.log", app.ProxyLogFile)]),
        new LogTabViewModel("Обход DPI",
        [
            new LogSource("service.log", Path.Combine(service.LogsDir, "service.log")),
            new LogSource("zapret.log", Path.Combine(service.LogsDir, "zapret.log")),
        ]),
    ];

    // Only the tab on screen is polled; another is read when it is chosen.
    partial void OnSelectedTabChanged(LogTabViewModel value) => value.Refresh();

    partial void OnFollowEndChanged(bool value)
    {
        foreach (LogTabViewModel tab in Tabs) tab.FollowEnd = value;
    }

    public override void OnShown()
    {
        SelectedTab.Refresh();
        _polling?.Dispose();
        _polling = _ticker.Start(PollEvery, () => SelectedTab.Refresh());
    }

    public override void OnHidden()
    {
        _polling?.Dispose();
        _polling = null;
    }

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
