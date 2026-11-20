using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using TgwsOne.Core;
using TgwsOne.Core.Zapret;
using TgwsOne.Services;

namespace TgwsOne.ViewModels;

/// <summary>Everything the strategy editor needs to know about the bypass on this machine: what a command line for it looks like.</summary>
internal sealed record ZapretContext(OsKind Os, ZapretBundle Bundle, ZapretLists Lists, string StateDir, string StrategiesDir);

/// <summary>
/// The editor of a custom strategy: a name, the desync part as text (one argument per line, like a <c>.args</c> file), a check that
/// runs the real <c>--dry-run</c> as the current user, and a save that goes through the elevated helper. Saving needs
/// administrator rights because the service runs what is saved as root / SYSTEM.
/// </summary>
internal sealed partial class CustomStrategyViewModel : ObservableObject
{
    public const string HelpUrl = "https://github.com/bol-van/zapret2/blob/v1.0.7/docs/manual.md";
    public const string NameRule = "Имя: латиница в нижнем регистре, цифры и дефис, до 32 символов.";
    public const string Hint = "По одному параметру в строке: --lua-desync=…, --blob=…, --lua-init=…, --payload=…, --out-range=…, --in-range=…. Строки с # — комментарии.";

    private readonly ZapretContext _zapret;
    private readonly ICommandRunner _runner;
    private readonly IElevatedHelper _helper;
    private readonly IClipboardService _clipboard;
    private readonly ILauncher _launcher;

    [ObservableProperty] private string _name;
    [ObservableProperty] private string _text;
    [ObservableProperty] private string? _nameError;
    [ObservableProperty] private string? _textError;
    [ObservableProperty] private string? _checkOutput;
    [ObservableProperty] private bool _checkPassed;
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowProblem))]
    private string? _problem;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasFallback), nameof(ShowProblem))]
    private string? _fallbackCommand;
    [ObservableProperty] private string? _notice;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(CheckCommand), nameof(SaveCommand))]
    private bool _isBusy;

    /// <param name="existingName">The strategy being edited, or null for a new one.</param>
    public CustomStrategyViewModel(string? existingName, string text, ZapretContext zapret, ICommandRunner runner, IElevatedHelper helper,
                                   IClipboardService clipboard, ILauncher launcher)
    {
        _name = existingName ?? "";
        _text = text;
        IsNew = existingName is null;
        _zapret = zapret;
        _runner = runner;
        _helper = helper;
        _clipboard = clipboard;
        _launcher = launcher;
    }

    public bool IsNew { get; }

    /// <summary>The name is fixed once the strategy exists: another name would be another strategy.</summary>
    public bool NameEditable => IsNew;

    public string Title => IsNew ? "Новая своя стратегия" : $"Своя стратегия «{Name}»";

    public bool HasFallback => FallbackCommand != null;

    /// <summary>A problem to show on its own line: with a command to run by hand the problem is told in that panel instead.</summary>
    public bool ShowProblem => Problem != null && FallbackCommand == null;

    /// <summary>True once the strategy was saved: the caller reloads the list (and re-applies it if it is the one in use).</summary>
    public bool Saved { get; private set; }

    /// <summary>The name of the strategy that was saved, for the caller.</summary>
    public string SavedName => Name;

    /// <summary>Raised when the window has nothing left to do and should close.</summary>
    public event Action? CloseRequested;

    /// <summary>Checks name and text the way the elevated side will; shows the first thing wrong under its field.</summary>
    /// <returns>The parsed desync part, or null when something is wrong.</returns>
    private DesyncPart? Validate()
    {
        NameError = null;
        TextError = null;
        Problem = null;
        if (!CustomStrategies.IsValidName(Name))
        {
            NameError = NameRule;
            return null;
        }
        (DesyncPart? part, string? error) = CustomStrategies.Parse(Text);
        if (part is null) TextError = error;
        return part;
    }

    /// <summary>
    /// Runs the real <c>--dry-run</c> on the full command line this strategy would have. It runs as the current user, which is
    /// not root: a file that only root can read passes on the service's side and fails here, so a failure is a pointer, not a verdict.
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanAct))]
    private async Task CheckAsync()
    {
        CheckOutput = null;
        CheckPassed = false;
        if (Validate() is not { } part) return;
        if (!_zapret.Bundle.Exists)
        {
            CheckOutput = $"Пакет zapret2 не найден: {_zapret.Bundle.Exe}";
            return;
        }

        IsBusy = true;
        try
        {
            IReadOnlyList<string> argv = ZapretArgs.Build(_zapret.Os, _zapret.Bundle, _zapret.Lists, "custom:" + Name, _zapret.StateDir, part);
            (int code, string output) = await _runner.RunAsync(_zapret.Bundle.Exe, ["--dry-run", .. argv]);
            CheckPassed = code == 0;
            string text = output.Trim();
            CheckOutput = code == 0
                ? (text.Length > 0 ? $"Проверка пройдена.\n{text}" : "Проверка пройдена.")
                : (text.Length > 0 ? text : $"Проверка завершилась с кодом {code}.");
        }
        finally
        {
            IsBusy = false;
        }
    }

    private bool CanAct() => !IsBusy;

    /// <summary>Asks for administrator rights and has the strategy written where the service reads it.</summary>
    [RelayCommand(CanExecute = nameof(CanAct))]
    private async Task SaveAsync()
    {
        FallbackCommand = null;
        Notice = null;
        if (Validate() is null) return;

        IsBusy = true;
        try
        {
            AdminOutcome outcome = await _helper.SaveStrategyAsync(Name, Text);
            if (outcome.Ok)
            {
                Saved = true;
                CloseRequested?.Invoke();
                return;
            }
            Problem = outcome.Message;
            FallbackCommand = outcome.FallbackCommand;
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private async Task CopyFallbackAsync()
    {
        if (FallbackCommand is null) return;
        await _clipboard.SetTextAsync(FallbackCommand);
        Notice = "Команда скопирована";
    }

    [RelayCommand]
    private async Task OpenHelpAsync()
    {
        if (!await _launcher.OpenAsync(HelpUrl)) Notice = $"Не удалось открыть справку: {HelpUrl}";
    }

    [RelayCommand]
    private void Cancel() => CloseRequested?.Invoke();
}
