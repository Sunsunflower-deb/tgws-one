using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using TgwsOne.Engine;

namespace TgwsOne.Mobile;

/// <summary>A message under the status: what happened and, usually, what to do about it.</summary>
public sealed record HomeBanner(string Text, Tone Tone);

/// <summary>
/// Главная on the phone: one button that starts or stops the engine and one phrase that says whether Telegram
/// works through it. The link actions live on the connection page, so this page stays to one decision.
/// </summary>
public sealed partial class HomeViewModel : ObservableObject
{
    private readonly IProxyEngineControl _proxy;
    private readonly IMobileSettings _settings;
    private readonly Func<long> _activeConnections;

    private EngineStartResult? _lastStart;

    [ObservableProperty] private string _headline = "";
    [ObservableProperty] private Tone _headlineTone = Tone.Grey;
    [ObservableProperty] private string _proxyLine = "";
    [ObservableProperty] private Tone _proxyTone = Tone.Grey;
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ButtonText))]
    [NotifyCanExecuteChangedFor(nameof(ToggleCommand))]
    private bool _isRunning;
    [ObservableProperty] private HomeBanner? _banner;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ToggleCommand))]
    private bool _isBusy;

    /// <param name="activeConnections">The engine's live client count; Android cannot read other apps' TCP table.</param>
    public HomeViewModel(IProxyEngineControl proxy, IMobileSettings settings, Func<long> activeConnections)
    {
        _proxy = proxy;
        _settings = settings;
        _activeConnections = activeConnections;
        Refresh();
    }

    public string ButtonText => IsRunning ? "Остановить" : "Запустить";

    public bool CanAct() => !IsBusy;

    /// <summary>Reads the engine again: the status line, the proxy line and which way the button now goes.</summary>
    public void Refresh()
    {
        EngineProxyStatus status = _proxy.GetStatus();
        IsRunning = status.Running;

        long connections = status.Listening ? _activeConnections() : 0;
        (Headline, HeadlineTone) = MobileStatusText.HomeLine(status, _lastStart, connections);
        (ProxyLine, ProxyTone) = MobileStatusText.ProxyLine(status, _settings.LoadProxy().Host.ToString());
    }

    [RelayCommand(CanExecute = nameof(CanAct))]
    private async Task ToggleAsync()
    {
        IsBusy = true;
        Banner = null;
        try
        {
            if (_proxy.GetStatus().Running)
                await StopAsync();
            else
                await StartAsync();
        }
        finally
        {
            IsBusy = false;
        }
        Refresh();
    }

    private async Task StartAsync()
    {
        EngineStartResult result;
        try
        {
            result = await _proxy.StartAsync();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Another start held the lock too long, or the files could not be written: no result to remember.
            Banner = new HomeBanner($"Не удалось запустить прокси: {ex.Message}", Tone.Red);
            return;
        }

        _lastStart = result;
        Banner = result.Outcome switch
        {
            EngineStartOutcome.PortSwitched => new HomeBanner(
                $"Порт {result.OldPort} занят — прокси на {result.Port}. Откройте ссылку в Telegram заново.", Tone.Amber),
            _ when !result.Ok => new HomeBanner(FailureDetails(result), Tone.Red),
            _ => null,
        };
    }

    private async Task StopAsync()
    {
        try
        {
            await _proxy.StopAsync();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Banner = new HomeBanner($"Не удалось остановить прокси: {ex.Message}", Tone.Red);
        }
    }

    private static string FailureDetails(EngineStartResult r)
    {
        string first = r.Output.Split('\n', 2)[0].Trim();
        string reason = MobileStatusText.StartFailure(r);
        return first.Length == 0
            ? $"Не удалось запустить прокси: {reason}."
            : $"Не удалось запустить прокси: {reason}. Движок сообщил: {first}";
    }
}
