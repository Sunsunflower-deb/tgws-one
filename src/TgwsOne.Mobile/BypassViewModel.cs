using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using TgwsOne.Core;
using TgwsOne.Engine.Desync;

namespace TgwsOne.Mobile;

/// <summary>One choice in the strategy list; <see cref="IsSelected"/> is only ever what the engine says.</summary>
public sealed partial class StrategyOption : ObservableObject
{
    [ObservableProperty]
    private bool _isSelected;

    public StrategyOption(string id, Func<StrategyOption, Task> choose)
    {
        Id = id;
        Name = MobileStatusText.StrategyName(id);
        ChooseCommand = new AsyncRelayCommand(() => choose(this));
    }

    public string Id { get; }
    public string Name { get; }
    public IAsyncRelayCommand ChooseCommand { get; }

    /// <summary>Pushes the value to the screen again when a click was refused and nothing else changed.</summary>
    public void Resync() => OnPropertyChanged(nameof(IsSelected));
}

/// <summary>A line of «Что выбрал Авто»: which strategy it settled on for a host and how often it had to move on.</summary>
public sealed record AutoRow(string Host, string StrategyName, string Failures);

/// <summary>
/// Обход DPI on the phone: the switch, the explicit strategies and the per-host table of what <c>Авто</c> chose.
/// The state belongs to the running engine's controller; the saved Android strategy keeps the choice for next start.
/// </summary>
public sealed partial class BypassViewModel : ObservableObject
{
    public const string EmptyAutoText = "Пока нет данных — откройте Telegram, Авто начнёт выбирать после первых соединений";

    private readonly IDesyncControl _control;
    private readonly IMobileSettings _settings;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ButtonText), nameof(CanChange))]
    [NotifyCanExecuteChangedFor(nameof(ToggleCommand), nameof(LoadCommand))]
    private bool _isBypassOn;

    [ObservableProperty] private string _statusLine = "";
    [ObservableProperty] private Tone _statusTone = Tone.Grey;
    [ObservableProperty] private string _currentStrategy = DesyncFinder.AutoId;
    [ObservableProperty] private bool _showAuto;
    [ObservableProperty] private bool _autoIsEmpty;
    [ObservableProperty] private string? _errorText;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanChange))]
    [NotifyCanExecuteChangedFor(nameof(ToggleCommand), nameof(LoadCommand))]
    private bool _isBusy;

    public BypassViewModel(IDesyncControl control, IMobileSettings settings)
    {
        _control = control;
        _settings = settings;

        Strategies.Add(new StrategyOption(DesyncFinder.AutoId, ChooseAsync));
        foreach (DesyncStrategy strategy in BuiltInDesync.BuiltIn)
            Strategies.Add(new StrategyOption(strategy.Id, ChooseAsync));
    }

    public ObservableCollection<StrategyOption> Strategies { get; } = [];
    public ObservableCollection<AutoRow> AutoRows { get; } = [];

    public string ButtonText => IsBypassOn ? "Выключить обход" : "Включить обход";

    public bool CanChange => !IsBusy;

    /// <summary>Reads the live controller: whether the bypass is on, which strategy it uses and what Авто chose.</summary>
    [RelayCommand]
    public async Task LoadAsync()
    {
        ErrorText = null;
        try
        {
            FinderBaseline baseline = await _control.CaptureAsync();
            IReadOnlyList<AutoHost> auto = _control.AutoHosts;

            IsBypassOn = baseline.Enabled && baseline.Strategy != DesyncFinder.NoBypassId;
            CurrentStrategy = baseline.Strategy;
            (StatusLine, StatusTone) = MobileStatusText.BypassLine(IsBypassOn, CurrentStrategy, auto);

            foreach (StrategyOption option in Strategies)
                option.IsSelected = IsBypassOn && option.Id == CurrentStrategy;

            ShowAuto = IsBypassOn && CurrentStrategy == DesyncFinder.AutoId;
            AutoRows.Clear();
            if (ShowAuto)
            {
                foreach (AutoHost host in auto)
                    AutoRows.Add(new AutoRow(host.Host, MobileStatusText.StrategyName(host.Strategy), FailuresText(host.Failures)));
            }
            AutoIsEmpty = ShowAuto && AutoRows.Count == 0;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            ErrorText = $"Не удалось прочитать состояние обхода: {ex.Message}";
        }
    }

    [RelayCommand(CanExecute = nameof(CanChange))]
    private async Task ToggleAsync()
    {
        if (IsBypassOn)
            await Run(() => _control.ApplyAsync(DesyncFinder.NoBypassId));
        else
            await Run(() => _control.ApplyAsync(EnabledStrategy()));
    }

    private string EnabledStrategy()
    {
        string strategy = _settings.LoadAndroid().Strategy;
        return strategy.Length == 0 || strategy == DesyncFinder.NoBypassId ? DesyncFinder.AutoId : strategy;
    }

    private async Task ChooseAsync(StrategyOption option)
    {
        if (!CanChange || (IsBypassOn && option.Id == CurrentStrategy))
        {
            option.Resync();
            return;
        }

        await Run(async () =>
        {
            string? problem = await _control.ApplyAsync(option.Id);
            if (problem == null)
            {
                AndroidSettings settings = _settings.LoadAndroid();
                _settings.SaveAndroid(settings with { Strategy = option.Id });
            }
            return problem;
        });

        foreach (StrategyOption o in Strategies) o.Resync();
    }

    /// <summary>Applies one change, shows the engine's refusal, then reads the controller again.</summary>
    private async Task Run(Func<Task<string?>> change)
    {
        IsBusy = true;
        ErrorText = null;
        string? problem;
        try
        {
            problem = await change();
        }
        finally
        {
            IsBusy = false;
        }

        await LoadAsync();
        if (problem != null) ErrorText = problem;   // the refresh clears old errors, not the one this change just got
    }

    private static string FailuresText(int failures)
        => failures == 0 ? "—" : $"{failures} {Russian.Plural(failures, "сбой", "сбоя", "сбоев")}";
}
