using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace TgwsOne.Mobile;

/// <summary>One line of the check list: a mark in the colour of its state, then what was found.</summary>
public sealed class CheckRowViewModel(CheckResult result)
{
    public string Title { get; } = result.Title;
    public string Detail { get; } = result.Detail;
    public bool HasDetail => Detail.Length > 0;
    public CheckState State { get; } = result.State;

    public Tone Tone { get; } = result.State switch
    {
        CheckState.Ok => Tone.Green,
        CheckState.Warn => Tone.Amber,
        CheckState.Fail => Tone.Red,
        _ => Tone.Grey,
    };
}

/// <summary>
/// Диагностика on the phone: one button, checks that fill in as they finish, and a verdict with the one thing to
/// do about it. The actions are the platform page's, so a fix from here behaves like the same button there.
/// </summary>
public sealed partial class DiagnosticsViewModel : ObservableObject
{
    private readonly Diagnostics _diagnostics;
    private readonly IDiagnosticsActions _actions;
    private VerdictAction _action = VerdictAction.None;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(RunText), nameof(ShowHint))]
    [NotifyCanExecuteChangedFor(nameof(RunCommand), nameof(DoActionCommand))]
    private bool _isRunning;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(RunText), nameof(ShowHint))]
    private bool _hasRun;

    [ObservableProperty] private string? _verdictText;
    [ObservableProperty] private string? _actionText;
    [ObservableProperty] private string? _errorText;

    public DiagnosticsViewModel(Diagnostics diagnostics, IDiagnosticsActions actions)
    {
        _diagnostics = diagnostics;
        _actions = actions;
        Rows.CollectionChanged += (_, _) => OnPropertyChanged(nameof(HasRows));
    }

    public ObservableCollection<CheckRowViewModel> Rows { get; } = [];

    public bool HasRows => Rows.Count > 0;
    public bool HasAction => ActionText != null;
    public string RunText => IsRunning ? "Проверяю…" : HasRun ? "Проверить ещё раз" : "Запустить";
    public bool ShowHint => !HasRun && !IsRunning;

    partial void OnActionTextChanged(string? value) => OnPropertyChanged(nameof(HasAction));

    /// <summary>Starts a run without waiting for it (another page can ask for it by opening this one).</summary>
    public void StartRun() => _ = RunCommand.ExecuteAsync(null);

    private bool CanRun() => !IsRunning;

    [RelayCommand(CanExecute = nameof(CanRun))]
    private async Task RunAsync()
    {
        IsRunning = true;
        ErrorText = null;
        VerdictText = null;
        ActionText = null;
        _action = VerdictAction.None;
        Rows.Clear();
        try
        {
            await foreach (CheckResult check in _diagnostics.RunAsync(CancellationToken.None))
                Rows.Add(new CheckRowViewModel(check));

            Verdict verdict = _diagnostics.Decide();
            VerdictText = verdict.Text;
            _action = verdict.Action;
            ActionText = ActionLabel(verdict.Action);
            HasRun = true;
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or UnauthorizedAccessException)
        {
            ErrorText = $"Проверка не удалась: {ex.Message}";
        }
        finally
        {
            IsRunning = false;
        }
    }

    private static string? ActionLabel(VerdictAction action) => action switch
    {
        VerdictAction.ChangePort => "Сменить порт",
        VerdictAction.Connect => "Подключить",
        VerdictAction.EnableBypass => "Включить обход",
        VerdictAction.FindStrategy => "Подобрать стратегию",
        VerdictAction.OpenInTelegram => "Открыть в Telegram",
        _ => null,
    };

    /// <summary>The button under the verdict. What changes the situation is followed by a new check, so it is not stale.</summary>
    [RelayCommand(CanExecute = nameof(CanRun))]
    private async Task DoActionAsync()
    {
        ErrorText = null;
        string? problem;
        switch (_action)
        {
            case VerdictAction.ChangePort:
                problem = await _actions.ChangePortAsync();
                if (problem != null) ErrorText = problem;
                return;
            case VerdictAction.OpenInTelegram:
                problem = await _actions.OpenInTelegramAsync();
                if (problem != null) ErrorText = problem;
                return;
            case VerdictAction.Connect:
                problem = await _actions.ConnectAsync();
                break;
            case VerdictAction.EnableBypass:
                problem = await _actions.EnableBypassAsync();
                break;
            case VerdictAction.FindStrategy:
                problem = await _actions.FindStrategyAsync();
                break;
            default:
                return;
        }

        await RunAsync();
        if (problem != null) ErrorText = problem;
    }
}
