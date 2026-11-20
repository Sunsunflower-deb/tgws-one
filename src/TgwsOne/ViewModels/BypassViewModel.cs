using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using TgwsOne.Core.Zapret;
using TgwsOne.Services;
using TgwsOne.Views;

namespace TgwsOne.ViewModels;

/// <summary>One choice in the strategy list. Chosen by clicking; <see cref="IsSelected"/> is only ever what the service says.</summary>
internal sealed partial class StrategyOption : ObservableObject
{
    [ObservableProperty]
    private bool _isSelected;

    public StrategyOption(string id, Func<StrategyOption, Task> choose)
    {
        Id = id;
        Name = StatusText.DisplayName(id);
        ChooseCommand = new AsyncRelayCommand(() => choose(this));
    }

    public string Id { get; }
    public string Name { get; }
    public IAsyncRelayCommand ChooseCommand { get; }

    /// <summary>Pushes <see cref="IsSelected"/> to the screen again: a radio button that was clicked has checked itself, and when the choice is refused the value does not change, so nothing would tell it to go back.</summary>
    public void Resync() => OnPropertyChanged(nameof(IsSelected));
}

/// <summary>A line of «Что выбрал Авто»: which strategy Авто settled on for a host and how often it had to move on.</summary>
internal sealed record AutoRow(string Host, string StrategyName, string Failures);

/// <summary>Обход DPI: the machine-wide bypass, which any user may switch, tune and set exclusions for.</summary>
internal sealed partial class BypassViewModel : PageViewModel
{
    public const string UnavailableText = "Обход DPI не установлен — нужен администратор";
    public const string EmptyAutoText = "Пока нет данных — откройте Telegram, Авто начнёт выбирать после первых соединений";

    private readonly IControlClient _control;
    private readonly StatusMonitor _monitor;
    private readonly IDialogService _dialogs;
    private readonly Func<FinderViewModel> _createFinder;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanChange))]
    [NotifyCanExecuteChangedFor(nameof(ToggleBypassCommand), nameof(SaveExclusionsCommand), nameof(FindBestCommand))]
    private bool _serviceReachable;

    [ObservableProperty] private bool _bypassOn;
    [ObservableProperty] private string _statusLine = "";
    [ObservableProperty] private Tone _statusTone = Tone.Grey;
    [ObservableProperty] private string _currentStrategy = BuiltInStrategies.Auto;
    [ObservableProperty] private bool _showAuto;
    [ObservableProperty] private bool _autoIsEmpty;
    /// <summary>What the service said when it turned something down (a command line that does not pass its check, say).</summary>
    [ObservableProperty] private string? _errorText;

    [ObservableProperty] private bool _isEditingExclusions;
    [ObservableProperty] private string _exclusionsSummary = "";
    [ObservableProperty] private string _hostsText = "";
    [ObservableProperty] private string _ipsText = "";
    [ObservableProperty] private string? _invalidText;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanChange))]
    [NotifyCanExecuteChangedFor(nameof(ToggleBypassCommand), nameof(SaveExclusionsCommand), nameof(FindBestCommand))]
    private bool _isBusy;

    private IReadOnlyList<string> _hosts = [];
    private IReadOnlyList<string> _ips = [];

    /// <param name="createFinder">A fresh finder for every run of «Подобрать лучшую…».</param>
    public BypassViewModel(IControlClient control, StatusMonitor monitor, IDialogService dialogs, Func<FinderViewModel> createFinder)
        : base(AppPageInfo.Title(AppPage.Bypass), AppPageInfo.Icon(AppPage.Bypass))
    {
        _control = control;
        _monitor = monitor;
        _dialogs = dialogs;
        _createFinder = createFinder;
        Apply(monitor.Current);
        monitor.Changed += Apply;
    }

    public ObservableCollection<StrategyOption> Strategies { get; } = [];
    public ObservableCollection<AutoRow> AutoRows { get; } = [];

    /// <summary>The page's controls are usable: there is a service and nothing is in flight.</summary>
    public bool CanChange => ServiceReachable && !IsBusy;

    public override void OnShown() => _ = LoadAsync();

    private bool CanAct() => CanChange;

    // ----- what the snapshots say -----

    private void Apply(AppSnapshot s)
    {
        ServiceReachable = s.ServiceReachable;
        (StatusLine, StatusTone) = StatusText.BypassLine(s);
        if (s.Bypass is not { } bypass) return;

        BypassOn = bypass.Enabled;
        if (IsBusy) return;   // an answer is on its way; what it says about the choice wins over this older snapshot

        CurrentStrategy = bypass.Strategy;
        foreach (StrategyOption option in Strategies) option.IsSelected = option.Id == CurrentStrategy;

        ShowAuto = bypass.Strategy == BuiltInStrategies.Auto;
        AutoRows.Clear();
        if (ShowAuto)
            foreach (AutoHost host in bypass.Auto ?? [])
                AutoRows.Add(new AutoRow(host.Host, StatusText.DisplayName(host.Strategy), FailuresText(host.Failures)));
        AutoIsEmpty = ShowAuto && AutoRows.Count == 0;
    }

    private static string FailuresText(int failures)
        => failures == 0 ? "—" : $"{failures} {Russian.Plural(failures, "сбой", "сбоя", "сбоев")}";

    // ----- loading -----

    /// <summary>Asks the service for its strategies and exclusions. Called when the page is shown.</summary>
    [RelayCommand]
    private async Task LoadAsync()
    {
        try
        {
            ControlResponse strategies = await _control.SendAsync(new ControlRequest(ControlCommands.Strategies));
            if (strategies is { Ok: true, Items: { } items })
            {
                Strategies.Clear();
                foreach (StrategyItem item in items)
                    Strategies.Add(new StrategyOption(item.Id, ChooseAsync) { IsSelected = item.Id == CurrentStrategy });
            }

            ControlResponse exclusions = await _control.SendAsync(new ControlRequest(ControlCommands.Exclusions));
            if (exclusions.Ok && !IsEditingExclusions) ShowExclusions(exclusions.Hosts ?? [], exclusions.Ips ?? []);
        }
        catch (ServiceUnavailableException)
        {
            // the status line says so; there is nothing more to load
        }
    }

    // ----- the switch and the strategy -----

    /// <summary>Turns the bypass on if it is off and off if it is on, for everyone on this machine.</summary>
    [RelayCommand(CanExecute = nameof(CanAct))]
    private async Task ToggleBypassAsync()
    {
        string command = BypassOn ? ControlCommands.Off : ControlCommands.On;
        await Run(() => _control.SendAsync(new ControlRequest(command)));
        OnPropertyChanged(nameof(BypassOn));   // a switch that was clicked has flipped itself; this puts it back when the service said no
    }

    private async Task ChooseAsync(StrategyOption option)
    {
        if (!CanChange || option.Id == CurrentStrategy)
        {
            option.Resync();
            return;
        }

        await Run(() => _control.SendAsync(new ControlRequest(ControlCommands.Use, Strategy: option.Id)));
        foreach (StrategyOption o in Strategies) o.Resync();   // refused or not, the screen shows what the service now has
    }

    /// <summary>
    /// «Подобрать лучшую…»: opens the finder, which switches strategies on the real bypass while it runs. The page stays as it was
    /// behind it (a busy page ignores the snapshots that show the strategy changing) and catches up when the window closes.
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanAct))]
    private async Task FindBestAsync()
    {
        IsBusy = true;
        ErrorText = null;
        try
        {
            await _dialogs.ShowFinderAsync(_createFinder());
        }
        finally
        {
            IsBusy = false;
        }
        await _monitor.RefreshNowAsync();
        Apply(_monitor.Current);
    }

    /// <summary>Sends one command, shows the service's refusal (if any), and brings the page in line with what the service reports afterwards.</summary>
    private async Task Run(Func<Task<ControlResponse>> send)
    {
        IsBusy = true;
        ErrorText = null;
        try
        {
            ControlResponse r = await send();
            if (!r.Ok) ErrorText = r.Error;
        }
        catch (ServiceUnavailableException ex)
        {
            ErrorText = ex.Message;
        }
        finally
        {
            IsBusy = false;
        }
        await _monitor.RefreshNowAsync();
        Apply(_monitor.Current);
    }

    // ----- exclusions -----

    private void ShowExclusions(IReadOnlyList<string> hosts, IReadOnlyList<string> ips)
    {
        _hosts = hosts;
        _ips = ips;
        ExclusionsSummary = hosts.Count + ips.Count == 0 ? "нет исключений" : Summary([.. hosts, .. ips]);
    }

    private static string Summary(IReadOnlyList<string> all)
    {
        const int shown = 4;
        string head = string.Join(" · ", all.Take(shown));
        return all.Count > shown ? $"{head} · ещё {all.Count - shown}" : head;
    }

    [RelayCommand]
    private void EditExclusions()
    {
        HostsText = string.Join('\n', _hosts);
        IpsText = string.Join('\n', _ips);
        InvalidText = null;
        IsEditingExclusions = true;
    }

    [RelayCommand]
    private void CancelExclusions()
    {
        IsEditingExclusions = false;
        InvalidText = null;
    }

    /// <summary>Sends both boxes to the service. Nothing is saved until every entry is valid: the refusal lists the ones that are not.</summary>
    [RelayCommand(CanExecute = nameof(CanAct))]
    private async Task SaveExclusionsAsync()
    {
        IsBusy = true;
        ErrorText = null;
        InvalidText = null;
        try
        {
            ControlResponse r = await _control.SendAsync(new ControlRequest(ControlCommands.SetExclusions, Hosts: Entries(HostsText), Ips: Entries(IpsText)));
            if (r.Ok)
            {
                ShowExclusions(r.Hosts ?? [], r.Ips ?? []);
                IsEditingExclusions = false;
            }
            else if (r.Invalid is { Count: > 0 } invalid)
            {
                InvalidText = $"Не удалось разобрать: {string.Join(" · ", invalid)}. Пока в списках есть неверные записи, ничего не сохраняется.";
            }
            else
            {
                ErrorText = r.Error;
            }
        }
        catch (ServiceUnavailableException ex)
        {
            ErrorText = ex.Message;
        }
        finally
        {
            IsBusy = false;
        }
    }

    // One entry per line, but commas, semicolons and spaces separate too (a pasted list is rarely tidy).
    private static string[] Entries(string text)
        => text.Split(['\n', '\r', ',', ';', ' ', '\t'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
}
