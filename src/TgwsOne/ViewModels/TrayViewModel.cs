using System.Collections.Specialized;
using System.Windows.Input;
using CommunityToolkit.Mvvm.Input;
using TgwsOne.Services;

namespace TgwsOne.ViewModels;

/// <summary>One line of the tray menu, as plain data; <see cref="TrayService"/> turns it into the platform's menu.</summary>
internal sealed record TrayItem(string Text, ICommand? Command = null, bool IsRadio = false, bool IsChecked = false, bool IsEnabled = true,
                                IReadOnlyList<TrayItem>? Children = null, bool IsSeparator = false)
{
    public static TrayItem Separator { get; } = new("", IsSeparator: true);

    /// <summary>Everything that shows, as text: two menus with the same signature look and behave the same, so the platform's menu need not be rebuilt.</summary>
    public string Signature
        => IsSeparator ? "-" : $"{Text}|{(IsRadio ? "r" : "")}{(IsChecked ? "c" : "")}{(IsEnabled ? "" : "x")}" +
                               (Children is { } children ? "[" + string.Join(';', children.Select(c => c.Signature)) + "]" : "");
}

/// <summary>
/// What the tray shows: a tooltip and a coloured dot (the headline's, from Главная), and a menu that does what the pages' own
/// buttons do — the items are those pages' commands, not copies of them. It reads the pages after they have read the snapshot
/// (they subscribed to the monitor first), so it never shows a state the window does not.
/// </summary>
internal sealed class TrayViewModel
{
    private readonly HomeViewModel _home;
    private readonly BypassViewModel _bypass;
    private readonly ICommand _show;
    private readonly ICommand _quit;
    private bool _loadingStrategies;

    public TrayViewModel(HomeViewModel home, BypassViewModel bypass, StatusMonitor monitor, Action show, Action quit)
    {
        _home = home;
        _bypass = bypass;
        _show = new RelayCommand(show);
        _quit = new RelayCommand(quit);
        monitor.Changed += _ => OnSnapshot();
        bypass.Strategies.CollectionChanged += OnStrategiesChanged;
    }

    /// <summary>Something the tray shows has (maybe) changed: the tooltip, the dot, the menu.</summary>
    public event Action? Changed;

    /// <summary>The headline of Главная.</summary>
    public string Tooltip => _home.Headline;

    public Tone Tone => _home.HeadlineTone;

    public IReadOnlyList<TrayItem> Items => Build();

    public string Signature => $"{Tooltip}|{Tone}|{string.Join('/', Items.Select(i => i.Signature))}";

    private void OnSnapshot()
    {
        LoadStrategiesOnce();
        Changed?.Invoke();
    }

    private void OnStrategiesChanged(object? sender, NotifyCollectionChangedEventArgs e) => Changed?.Invoke();

    // The page loads its list when it is shown; the tray has to have it before anyone looked at that page.
    private void LoadStrategiesOnce()
    {
        if (_loadingStrategies || !_bypass.ServiceReachable || _bypass.Strategies.Count > 0) return;
        _loadingStrategies = true;
        _ = LoadAsync();

        async Task LoadAsync()
        {
            try { await _bypass.LoadCommand.ExecuteAsync(null); }
            finally { _loadingStrategies = false; }
        }
    }

    private IReadOnlyList<TrayItem> Build() =>
    [
        new("Показать", _show),
        TrayItem.Separator,
        _home.IsProxyRunning ? new("Отключить", _home.DisconnectCommand) : new("Подключить", _home.ConnectCommand),
        new("Открыть в Telegram", _home.OpenInTelegramCommand),
        new("Копировать ссылку", _home.CopyLinkCommand),
        BypassMenu(),
        TrayItem.Separator,
        new("Выход", _quit),
    ];

    private TrayItem BypassMenu()
    {
        if (!_bypass.ServiceReachable) return new("Обход DPI", IsEnabled: false);

        var children = new List<TrayItem> { new(_bypass.BypassOn ? "Выключить" : "Включить", _bypass.ToggleBypassCommand) };
        if (_bypass.Strategies.Count > 0)
        {
            children.Add(TrayItem.Separator);
            children.AddRange(_bypass.Strategies.Select(o => new TrayItem(o.Name, o.ChooseCommand, IsRadio: true, IsChecked: o.IsSelected)));
        }
        return new("Обход DPI", Children: children);
    }
}
