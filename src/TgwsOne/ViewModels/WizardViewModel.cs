using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using TgwsOne.Services;

namespace TgwsOne.ViewModels;

internal enum WizardStep { Connect, Telegram, Done }

/// <summary>
/// The first-run wizard, shown once: connect, get Telegram to use the proxy, choose whether to start with the system. It does
/// nothing the Главная page does not: the connection is that page's «Подключить», so what the wizard shows is what a user would
/// otherwise have to find out by pressing buttons. However it ends (finished, skipped, window closed) it is not shown again.
/// </summary>
internal sealed partial class WizardViewModel : ObservableObject
{
    public const string TelegramHint = "Telegram спросит «Подключить прокси?» — нажмите «Подключить»";
    public const int StepCount = 3;

    private readonly IDiagnosticsActions _actions;
    private readonly StatusMonitor _monitor;
    private readonly Diagnostics _diagnostics;
    private readonly IAppSettings _settings;
    private readonly IAutostart _autostart;

    private bool _resolved;
    private bool _autostartFailed;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsConnectStep), nameof(IsTelegramStep), nameof(IsDoneStep), nameof(StepText), nameof(ShowNext), nameof(ShowSkip))]
    private WizardStep _step = WizardStep.Connect;

    // ----- step 1: the connection -----
    [ObservableProperty] private string _proxyLine = "";
    [ObservableProperty] private Tone _proxyTone = Tone.Grey;
    [ObservableProperty] private string _bypassLine = "";
    [ObservableProperty] private Tone _bypassTone = Tone.Grey;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(StartCommand), nameof(NextCommand))]
    private bool _isConnecting;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(NextCommand))]
    [NotifyPropertyChangedFor(nameof(ConnectFailed))]
    private bool _connectFinished;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ConnectFailed))]
    private bool _connected;

    /// <summary>What went wrong and what to do about it: the connection's own message, then the diagnostics verdict.</summary>
    [ObservableProperty] private string? _problem;

    // ----- step 2: Telegram -----
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowNext), nameof(ShowSkip))]
    private bool _telegramConnected;

    // ----- step 3 -----
    [ObservableProperty] private bool _autostartOn = true;
    [ObservableProperty] private string? _notice;

    public WizardViewModel(IDiagnosticsActions actions, StatusMonitor monitor, Diagnostics diagnostics, IAppSettings settings, IAutostart autostart)
    {
        _actions = actions;
        _monitor = monitor;
        _diagnostics = diagnostics;
        _settings = settings;
        _autostart = autostart;
        Apply(monitor.Current);
        monitor.Changed += Apply;
    }

    public bool IsConnectStep => Step == WizardStep.Connect;
    public bool IsTelegramStep => Step == WizardStep.Telegram;
    public bool IsDoneStep => Step == WizardStep.Done;

    public string StepText => $"Шаг {(int)Step + 1} из {StepCount}";

    /// <summary>The connection was tried and did not come up.</summary>
    public bool ConnectFailed => ConnectFinished && !Connected;

    /// <summary>«Далее»: after the connection step, and on the Telegram step once Telegram has connected (until then it is «Пропустить»).</summary>
    public bool ShowNext => IsConnectStep || (IsTelegramStep && TelegramConnected);

    public bool ShowSkip => IsTelegramStep && !TelegramConnected;

    /// <summary>Raised when the wizard is over and its window should close.</summary>
    public event Action? CloseRequested;

    private void Apply(AppSnapshot s)
    {
        (ProxyLine, ProxyTone) = StatusText.ProxyLine(s.Proxy, _settings.Load().Host.ToString());
        (BypassLine, BypassTone) = StatusText.BypassLine(s);
        TelegramConnected = s.TelegramConnections > 0;
    }

    // ----- step 1 -----

    private bool CanStart() => !IsConnecting;

    /// <summary>Connects (what «Подключить» does on Главная); when it does not come up, asks the diagnostics what is wrong.</summary>
    [RelayCommand(CanExecute = nameof(CanStart))]
    private async Task StartAsync()
    {
        IsConnecting = true;
        ConnectFinished = false;
        Problem = null;
        try
        {
            string? failure = await _actions.ConnectAsync();
            await _monitor.RefreshNowAsync();
            Connected = failure == null && _monitor.Current.Proxy.Listening;
            if (!Connected) Problem = await ExplainAsync(failure);
        }
        finally
        {
            ConnectFinished = true;
            IsConnecting = false;
        }
    }

    private async Task<string> ExplainAsync(string? failure)
    {
        string verdict;
        try
        {
            await foreach (CheckResult _ in _diagnostics.RunAsync(CancellationToken.None)) { }
            verdict = _diagnostics.Decide().Text;
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or UnauthorizedAccessException)
        {
            verdict = "Проверка не удалась.";
        }
        return failure is null ? verdict : $"{failure}\n{verdict}";
    }

    // ----- moving on -----

    private bool CanNext() => Step switch
    {
        WizardStep.Connect => ConnectFinished && !IsConnecting,
        WizardStep.Telegram => true,   // «Пропустить» is the same step forward when Telegram has not connected
        _ => false,
    };

    [RelayCommand(CanExecute = nameof(CanNext))]
    private void Next()
    {
        Notice = null;
        Step = Step switch
        {
            WizardStep.Connect => WizardStep.Telegram,
            WizardStep.Telegram => WizardStep.Done,
            _ => Step,
        };
        NextCommand.NotifyCanExecuteChanged();
    }

    // ----- step 2 -----

    [RelayCommand]
    private async Task OpenTelegramAsync()
    {
        Notice = await _actions.OpenInTelegramAsync();
    }

    // ----- the end -----

    /// <summary>
    /// «Готово»: the wizard is not shown again, and the autostart box is applied (it is on by default). A box that cannot be
    /// applied (a read-only profile) says so and leaves the wizard open once; the second press ends it anyway.
    /// </summary>
    [RelayCommand]
    private void Finish()
    {
        Resolve();
        if (!_autostartFailed)
        {
            try
            {
                _autostart.Set(AutostartOn);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                _autostartFailed = true;
                Notice = $"Не удалось настроить автозапуск: {ex.Message}. Его можно включить позже в настройках.";
                return;
            }
        }
        CloseRequested?.Invoke();
    }

    /// <summary>
    /// The window is closed, by «Готово» or the cross or Esc: the wizard has had its chance (a close without «Готово» leaves autostart
    /// as it was), and it stops following the monitor.
    /// </summary>
    public void Dismiss()
    {
        Resolve();
        _monitor.Changed -= Apply;
    }

    private void Resolve()
    {
        if (_resolved) return;
        _resolved = true;
        _settings.MarkFirstRunDone();
    }
}
