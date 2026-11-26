using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace TgwsOne.Mobile;

/// <summary>The pages the phone's one-window shell can show.</summary>
public enum MobilePage { Connect, Home, Bypass, Finder, Diagnostics, Logs, Settings }

/// <summary>
/// The phone shell: one page at a time, a menu, and a back stack that the system's back button walks. On the very
/// first launch it starts on «Подключение к Telegram»; «Продолжить» (or leaving that page) remembers the visit.
/// </summary>
public sealed partial class MobileShellViewModel : ObservableObject
{
    private readonly Stack<object> _history = new();
    private readonly Action? _firstRunDone;

    [ObservableProperty] private object _current = new();
    [ObservableProperty] private string _title = "";
    [ObservableProperty] private bool _canGoBack;
    [ObservableProperty] private bool _isFirstRun;
    [ObservableProperty] private bool _showFirstRunContinue;

    public MobileShellViewModel(HomeViewModel home, BypassViewModel bypass, FinderViewModel finder,
                                DiagnosticsViewModel diagnostics, LogsViewModel logs, SettingsViewModel settings,
                                ConnectViewModel connect, bool firstRun = false, Action? firstRunDone = null)
    {
        Home = home;
        Bypass = bypass;
        Finder = finder;
        Diagnostics = diagnostics;
        Logs = logs;
        Settings = settings;
        Connect = connect;
        _firstRunDone = firstRunDone;
        _isFirstRun = firstRun;
        _showFirstRunContinue = firstRun;

        finder.CloseRequested += () => GoBack();

        if (firstRun)
            Show(connect, push: false);
        else
            Show(home, push: false);
    }

    public HomeViewModel Home { get; }
    public BypassViewModel Bypass { get; }
    public FinderViewModel Finder { get; }
    public DiagnosticsViewModel Diagnostics { get; }
    public LogsViewModel Logs { get; }
    public SettingsViewModel Settings { get; }
    public ConnectViewModel Connect { get; }

    public static string TitleFor(object page) => page switch
    {
        HomeViewModel => "Главная",
        BypassViewModel => "Обход DPI",
        FinderViewModel => "Подбор стратегии",
        DiagnosticsViewModel => "Диагностика",
        LogsViewModel => "Журналы",
        SettingsViewModel => "Настройки",
        ConnectViewModel => "Подключение к Telegram",
        _ => "TgwsOne",
    };

    /// <summary>The system's back button: closes the current page. False when there is nothing to close.</summary>
    public bool GoBack()
    {
        if (_history.Count == 0) return false;
        Show(_history.Pop(), push: false);
        return true;
    }

    /// <summary>Opens the strategy finder from another page (the diagnostics verdict does this).</summary>
    public void ShowFinder() => NavigateTo(MobilePage.Finder);

    [RelayCommand]
    private void Back() => GoBack();

    [RelayCommand]
    private void Continue()
    {
        CompleteFirstRun();
        _history.Clear();
        Show(Home, push: false);
    }

    [RelayCommand]
    private void Navigate(string page)
    {
        if (Enum.TryParse(page, ignoreCase: true, out MobilePage target))
            NavigateTo(target);
    }

    private void NavigateTo(MobilePage page)
    {
        object target = PageFor(page);
        if (ReferenceEquals(target, Current)) return;
        if (IsFirstRun) CompleteFirstRun();
        Show(target, push: true);
    }

    private object PageFor(MobilePage page) => page switch
    {
        MobilePage.Connect => Connect,
        MobilePage.Home => Home,
        MobilePage.Bypass => Bypass,
        MobilePage.Finder => Finder,
        MobilePage.Diagnostics => Diagnostics,
        MobilePage.Logs => Logs,
        MobilePage.Settings => Settings,
        _ => Home,
    };

    private void Show(object page, bool push)
    {
        if (push) _history.Push(Current);

        if (Current is LogsViewModel oldLogs) oldLogs.OnHidden();
        Current = page;
        Title = TitleFor(page);
        CanGoBack = _history.Count > 0;
        ShowFirstRunContinue = IsFirstRun && page is ConnectViewModel;

        switch (page)
        {
            case LogsViewModel logs:
                logs.OnShown();
                break;
            case BypassViewModel bypass:
                _ = bypass.LoadAsync();
                break;
            case SettingsViewModel settings:
                settings.Reload();
                break;
            case ConnectViewModel connect:
                connect.Refresh();
                break;
            case HomeViewModel home:
                home.Refresh();
                break;
        }
    }

    private void CompleteFirstRun()
    {
        if (!IsFirstRun) return;
        IsFirstRun = false;
        ShowFirstRunContinue = false;
        _firstRunDone?.Invoke();
    }
}
