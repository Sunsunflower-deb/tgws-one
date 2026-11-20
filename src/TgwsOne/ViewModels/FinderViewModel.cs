using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using TgwsOne.Core.Zapret;
using TgwsOne.Services;

namespace TgwsOne.ViewModels;

internal enum FinderPhase { Confirm, Running, Done }

/// <summary>A row of the result table.</summary>
internal sealed partial class FinderRowViewModel(FinderRow row) : ObservableObject
{
    public FinderRow Row { get; } = row;
    public string Name { get; } = StatusText.DisplayName(row.StrategyId);
    public string? Error => Row.Error;
    public bool HasError => Row.Error != null;
    public string ErrorLine => $"ошибка: {Row.Error}";
    public string TelegramText => HasError ? "ошибка" : $"{Row.TelegramOk}/{StrategyFinder.TelegramHosts.Count}";
    public bool CloudflareOk => Row.CloudflareOk;
    public string TimeText => Row.Median is { } m ? $"{(int)Math.Round(m.TotalMilliseconds)} мс" : "—";

    [ObservableProperty]
    private bool _isBest;
}

/// <summary>
/// «Подобрать лучшую…»: asks first, then tries the strategies and shows how each did. Whatever the user does, the bypass ends up
/// either on the chosen strategy or exactly as it was: cancelling, «Вернуть как было» and closing the window all put it back.
/// </summary>
internal sealed partial class FinderViewModel : ObservableObject
{
    public const string ConfirmText = "Подбор займёт около минуты и на это время переключит обход для всех пользователей этого компьютера.";
    public const string AllRefusedVerdict = "Служба не смогла применить ни одну стратегию — подробности в журнале «Обход DPI».";

    private readonly StrategyFinder _finder;
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

    public FinderViewModel(StrategyFinder finder)
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

    /// <summary>The window may close without anything left to put back.</summary>
    public bool CanCloseNow => Phase == FinderPhase.Confirm || _resolved;

    public bool HasBest => _best != null;

    /// <summary>Raised when the dialog has nothing left to do and should close itself.</summary>
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
        _run = new CancellationTokenSource();
        CancellationToken ct = _run.Token;
        try
        {
            _baseline = await _finder.CaptureAsync(ct);
            IReadOnlyList<string> candidates = await _finder.CandidatesAsync(ct);
            Progress = candidates.Count > 0 ? Trying(candidates[0]) : "";

            int done = 0;
            await foreach (FinderRow row in _finder.RunAsync(candidates, _baseline, ct))
            {
                Rows.Add(new FinderRowViewModel(row));
                done++;
                Progress = done < candidates.Count ? Trying(candidates[done]) : "Заканчиваю…";
            }
            Finish();
        }
        catch (OperationCanceledException)
        {
            await ResolveByRestoringAsync();
            CloseRequested?.Invoke();
        }
        catch (Exception ex) when (ex is ServiceUnavailableException or FinderException)
        {
            ErrorText = ex.Message;
            await ResolveByRestoringAsync();
            Finish();
        }
    }

    private static string Trying(string id) => $"Проверяю: {StatusText.DisplayName(id)}…";

    private void Finish()
    {
        _best = StrategyFinder.Best([.. Rows.Select(r => r.Row)]);
        foreach (FinderRowViewModel r in Rows) r.IsBest = _best != null && r.Row == _best;
        Verdict = _best != null ? $"Лучшая стратегия — {StatusText.DisplayName(_best.StrategyId)}."
                : Rows.Count > 0 && Rows.All(r => r.HasError) ? AllRefusedVerdict
                : Rows.Count > 0 ? StrategyFinder.NoTelegramVerdict
                : null;
        ApplyText = _best != null ? $"Применить «{StatusText.DisplayName(_best.StrategyId)}»" : "";
        Progress = "";
        Phase = FinderPhase.Done;
        OnPropertyChanged(nameof(HasBest));
        OnPropertyChanged(nameof(CanCloseNow));
        ApplyCommand.NotifyCanExecuteChanged();
        RestoreCommand.NotifyCanExecuteChanged();
    }

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
            string? problem = await _finder.ApplyAsync(best.StrategyId, CancellationToken.None);
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

    /// <summary>The «Отмена» button: before the run it just closes, during it stops and restores.</summary>
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

    /// <summary>
    /// What closing the window does: nothing before the run, a stop and restore during it, a restore after it unless the user
    /// chose a strategy. The window asks this and closes itself once <see cref="CanCloseNow"/>.
    /// </summary>
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

    // Puts the bypass back as it was. On failure the message stays on screen and the window may still be closed.
    private async Task ResolveByRestoringAsync()
    {
        if (_baseline is { } baseline)
        {
            string? problem = await _finder.RestoreAsync(baseline, CancellationToken.None);
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
