using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace TgwsOne.Mobile;

public enum FinderPhase { Confirm, Running, Done }

/// <summary>A row of the result table.</summary>
public sealed partial class FinderRowViewModel(FinderRow row) : ObservableObject
{
    public FinderRow Row { get; } = row;
    public string Name { get; } = MobileStatusText.StrategyName(row.StrategyId);
    public bool HasError => Row.Error != null;
    public string ErrorLine => $"ошибка: {Row.Error}";
    public string TelegramText => HasError ? "ошибка" : $"{Row.TelegramOk}/{DesyncFinder.TelegramHosts.Count}";
    public bool CloudflareOk => Row.CloudflareOk;
    public bool SelfCheckOk => Row.SelfCheckOk;
    public string TimeText => Row.Median is { } m ? $"{(int)Math.Round(m.TotalMilliseconds)} мс" : "—";

    [ObservableProperty]
    private bool _isBest;
}

/// <summary>
/// «Подобрать лучшую…» on the phone: tries the operator's strategies on the real bypass and shows how each did.
/// Whatever the user does, the bypass ends up on the chosen strategy or exactly as it was.
/// </summary>
public sealed partial class FinderViewModel : ObservableObject
{
    public const string ConfirmText = "Подбор займёт около минуты и на это время переключит обход.";
    public const string AllRefusedVerdict = "Не удалось применить ни одну стратегию — подробности в журнале.";

    private readonly DesyncFinder _finder;
    private CancellationTokenSource? _run;
    private Task? _runTask;
    private FinderBaseline? _baseline;
    private FinderRow? _best;
    private bool _resolved;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsConfirm), nameof(IsRunning), nameof(IsDone), nameof(ShowTable), nameof(CanCloseNow))]
    private FinderPhase _phase = FinderPhase.Confirm;

    [ObservableProperty] private string _progress = "";
    [ObservableProperty] private string? _verdict;
    [ObservableProperty] private string? _errorText;
    [ObservableProperty] private string _applyText = "";

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ApplyCommand), nameof(RestoreCommand))]
    private bool _isBusy;

    public FinderViewModel(DesyncFinder finder)
    {
        _finder = finder;
    }

    public ObservableCollection<FinderRowViewModel> Rows { get; } = [];

    /// <summary>True when the user chose a strategy (the caller then knows the bypass changed on purpose).</summary>
    public bool Applied { get; private set; }

    public bool IsConfirm => Phase == FinderPhase.Confirm;
    public bool IsRunning => Phase == FinderPhase.Running;
    public bool IsDone => Phase == FinderPhase.Done;
    public bool ShowTable => Phase != FinderPhase.Confirm;
    public bool CanCloseNow => Phase == FinderPhase.Confirm || _resolved;
    public bool HasBest => _best != null;

    /// <summary>Raised when the page has nothing left to do and may close itself.</summary>
    public event Action? CloseRequested;

    // ----- the run -----

    [RelayCommand]
    private Task StartAsync() => _runTask = RunAsync();

    private async Task RunAsync()
    {
        Phase = FinderPhase.Running;
        ErrorText = null;
        Verdict = null;
        Rows.Clear();
        _resolved = false;
        _best = null;
        _run = new CancellationTokenSource();
        CancellationToken ct = _run.Token;
        try
        {
            _baseline = await _finder.CaptureAsync(ct);
            IReadOnlyList<string> candidates = _finder.Candidates();
            Progress = candidates.Count > 0 ? Trying(DesyncFinder.NoBypassId) : "";

            int done = 0;
            await foreach (FinderRow row in _finder.RunAsync(candidates, _baseline, ct))
            {
                Rows.Add(new FinderRowViewModel(row));
                done++;
                if (done < candidates.Count + 1)
                    Progress = Trying(candidates[done - 1]);
                else
                    Progress = "Заканчиваю…";
            }
            Finish();
        }
        catch (OperationCanceledException)
        {
            await ResolveByRestoringAsync();
            CloseRequested?.Invoke();
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or UnauthorizedAccessException)
        {
            ErrorText = ex.Message;
            await ResolveByRestoringAsync();
            Finish();
        }
    }

    private void Finish()
    {
        FinderRow? baseline = Rows.Count > 0 && Rows[0].Row.StrategyId == DesyncFinder.NoBypassId ? Rows[0].Row : null;
        IReadOnlyList<FinderRow> measured = [.. Rows.Where(r => r.Row.StrategyId != DesyncFinder.NoBypassId).Select(r => r.Row)];
        _best = DesyncFinder.Best(measured);

        foreach (FinderRowViewModel row in Rows)
            row.IsBest = _best != null && row.Row == _best;

        Verdict = _best != null ? $"Лучшая стратегия — {MobileStatusText.StrategyName(_best.StrategyId)}."
            : baseline is { } b && DesyncFinder.NoBypassNeeded(b) ? DesyncFinder.NoBypassNeededVerdict
            : Rows.Count > 0 && Rows.All(r => r.HasError) ? AllRefusedVerdict
            : Rows.Count > 0 ? DesyncFinder.NoTelegramVerdict
            : null;
        ApplyText = _best != null ? $"Применить «{MobileStatusText.StrategyName(_best.StrategyId)}»" : "";
        Progress = "";
        Phase = FinderPhase.Done;
        OnPropertyChanged(nameof(HasBest));
        OnPropertyChanged(nameof(CanCloseNow));
        ApplyCommand.NotifyCanExecuteChanged();
        RestoreCommand.NotifyCanExecuteChanged();
    }

    private static string Trying(string id) => $"Проверяю: {MobileStatusText.StrategyName(id)}…";

    // ----- what the user does with the result -----

    private bool CanResolve() => Phase == FinderPhase.Done && !IsBusy && !_resolved;

    private bool CanApply() => CanResolve() && _best != null;

    [RelayCommand(CanExecute = nameof(CanApply))]
    private async Task ApplyAsync()
    {
        if (_best is not { } best) return;
        IsBusy = true;
        ErrorText = null;
        try
        {
            string? problem = await _finder.ApplyAsync(best.StrategyId);
            if (problem != null)
            {
                ErrorText = problem;
                return;
            }
            Applied = true;
            Resolved();
        }
        finally
        {
            IsBusy = false;
        }
        CloseRequested?.Invoke();
    }

    [RelayCommand(CanExecute = nameof(CanResolve))]
    private async Task RestoreAsync()
    {
        IsBusy = true;
        ErrorText = null;
        try
        {
            await ResolveByRestoringAsync();
        }
        finally
        {
            IsBusy = false;
        }
        if (ErrorText == null) CloseRequested?.Invoke();
    }

    /// <summary>«Отмена»: before the run it just closes; during it stops and restores; after it restores.</summary>
    [RelayCommand]
    private void Cancel()
    {
        switch (Phase)
        {
            case FinderPhase.Running:
                _run?.Cancel();
                break;
            case FinderPhase.Confirm:
                CloseRequested?.Invoke();
                break;
            default:
                _ = RestoreCommand.ExecuteAsync(null);
                break;
        }
    }

    /// <summary>What closing the page does: nothing before the run, stop and restore during it, restore after it.</summary>
    public async Task ResolveAsync()
    {
        if (Phase == FinderPhase.Running)
        {
            _run?.Cancel();
            if (_runTask != null) await _runTask;
        }
        else if (Phase == FinderPhase.Done && !_resolved)
        {
            await ResolveByRestoringAsync();
        }
    }

    private async Task ResolveByRestoringAsync()
    {
        if (_baseline is { } baseline)
        {
            string? problem = await _finder.RestoreAsync(baseline);
            if (problem != null) ErrorText = problem;
        }
        Resolved();
    }

    private void Resolved()
    {
        _resolved = true;
        OnPropertyChanged(nameof(CanCloseNow));
        ApplyCommand.NotifyCanExecuteChanged();
        RestoreCommand.NotifyCanExecuteChanged();
    }
}
