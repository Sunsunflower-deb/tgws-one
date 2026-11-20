using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using TgwsOne.Core;
using TgwsOne.Core.Zapret;
using TgwsOne.Services;
using TgwsOne.Views;

namespace TgwsOne.ViewModels;

/// <summary>What the button under the verdict can do on other pages. Each returns the reason it could not, or null.</summary>
internal interface IDiagnosticsActions
{
    Task<string?> ConnectAsync();
    Task<string?> OpenInTelegramAsync();
    Task<string?> EnableBypassAsync();
    Task<string?> FindStrategyAsync();
}

/// <summary>One line of the check list: a mark in the colour of its state, then what was found.</summary>
internal sealed class CheckRowViewModel(CheckResult result)
{
    public string Title { get; } = result.Title;
    public string Detail { get; } = result.Detail;
    public bool HasDetail => Detail.Length > 0;

    public string IconPath { get; } = result.State switch
    {
        CheckState.Ok => IconPaths.Check,
        CheckState.Warn => IconPaths.Warning,
        CheckState.Fail => IconPaths.Error,
        _ => IconPaths.Dash,
    };

    public Tone Tone { get; } = result.State switch
    {
        CheckState.Ok => Tone.Green,
        CheckState.Warn => Tone.Amber,
        CheckState.Fail => Tone.Red,
        _ => Tone.Grey,
    };
}

/// <summary>Диагностика: one button, a list of checks that fills in as they finish, and a verdict with the one thing to do about it.</summary>
internal sealed partial class DiagnosticsViewModel : PageViewModel
{
    private readonly Diagnostics _diagnostics;
    private readonly INavigator _navigator;
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

    public DiagnosticsViewModel(Diagnostics diagnostics, INavigator navigator, IDiagnosticsActions actions)
        : base(AppPageInfo.Title(AppPage.Diagnostics), AppPageInfo.Icon(AppPage.Diagnostics))
    {
        _diagnostics = diagnostics;
        _navigator = navigator;
        _actions = actions;
        Rows.CollectionChanged += (_, _) => OnPropertyChanged(nameof(HasRows));
    }

    public ObservableCollection<CheckRowViewModel> Rows { get; } = [];

    public bool HasRows => Rows.Count > 0;

    public bool HasAction => ActionText != null;

    public string RunText => IsRunning ? "Проверяю…" : HasRun ? "Проверить ещё раз" : "Запустить";

    /// <summary>The explanation of what the check does: only until the first run has something better to show.</summary>
    public bool ShowHint => !HasRun && !IsRunning;

    partial void OnActionTextChanged(string? value) => OnPropertyChanged(nameof(HasAction));

    /// <summary>Starts a run without waiting for it (a button elsewhere asked for it by navigating here).</summary>
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

    /// <summary>The button under the verdict. What changes the situation is followed by a new check, so the verdict is not left stale.</summary>
    [RelayCommand(CanExecute = nameof(CanRun))]
    private async Task DoActionAsync()
    {
        ErrorText = null;
        string? problem;
        switch (_action)
        {
            case VerdictAction.ChangePort:
                _navigator.Show(AppPage.Settings);
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

/// <summary>The real actions: the commands of the other pages, so a fix from here behaves exactly like the same button there.</summary>
internal sealed class DiagnosticsActions(HomeViewModel home, BypassViewModel bypass, IControlClient control, StatusMonitor monitor) : IDiagnosticsActions
{
    public async Task<string?> ConnectAsync()
    {
        await home.ConnectCommand.ExecuteAsync(null);
        return home.Banner is { Tone: Tone.Red } banner ? banner.Text : null;
    }

    public async Task<string?> OpenInTelegramAsync()
    {
        await home.OpenInTelegramCommand.ExecuteAsync(null);
        return home.Banner is { Action: BannerAction.CopyLink } banner ? banner.Text : null;
    }

    public async Task<string?> EnableBypassAsync()
    {
        try
        {
            ControlResponse r = await control.SendAsync(new ControlRequest(ControlCommands.On));
            await monitor.RefreshNowAsync();
            return r.Ok ? null : $"Обход DPI не включился: {r.Error}";
        }
        catch (ServiceUnavailableException ex)
        {
            return ex.Message;
        }
    }

    public async Task<string?> FindStrategyAsync()
    {
        await bypass.FindBestCommand.ExecuteAsync(null);
        return null;
    }
}
