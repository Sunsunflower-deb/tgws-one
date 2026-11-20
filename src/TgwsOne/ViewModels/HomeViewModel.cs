using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using TgwsOne.Core;
using TgwsOne.Core.Zapret;
using TgwsOne.Services;
using TgwsOne.Views;

namespace TgwsOne.ViewModels;

/// <summary>What the button on a <see cref="HomeBanner"/> does.</summary>
internal enum BannerAction { None, OpenInTelegram, CopyLink, Restart }

/// <summary>A message under the status cards: what happened and, usually, what to do about it.</summary>
internal sealed record HomeBanner(string Text, Tone Tone, BannerAction Action = BannerAction.None)
{
    public string? ActionText => Action switch
    {
        BannerAction.OpenInTelegram => "Открыть в Telegram",
        BannerAction.CopyLink => "Копировать ссылку",
        BannerAction.Restart => "Перезапустить",
        _ => null,
    };

    public bool HasAction => Action != BannerAction.None;

    /// <summary>The mark in front of the text: an alarm for red, a warning for amber, plain information otherwise.</summary>
    public string IconPath => Tone switch
    {
        Tone.Red => IconPaths.Error,
        Tone.Amber => IconPaths.Warning,
        _ => IconPaths.Info,
    };
}

/// <summary>Главная: one place to see whether Telegram works and to connect or disconnect.</summary>
internal sealed partial class HomeViewModel : PageViewModel
{
    private readonly IProxyControl _proxy;
    private readonly IControlClient _control;
    private readonly StatusMonitor _monitor;
    private readonly IAppSettings _settings;
    private readonly ILauncher _launcher;
    private readonly IClipboardService _clipboard;
    private readonly IDialogService _dialogs;
    private readonly INavigator _navigator;
    private readonly Func<QrViewModel> _createQr;

    [ObservableProperty] private string _headline = "";
    [ObservableProperty] private Tone _headlineTone = Tone.Grey;
    [ObservableProperty] private string _proxyLine = "";
    [ObservableProperty] private Tone _proxyTone = Tone.Grey;
    [ObservableProperty] private string _bypassLine = "";
    [ObservableProperty] private Tone _bypassTone = Tone.Grey;
    /// <summary>The bypass line leads to Обход DPI (the chevron): only when there is a service to look at.</summary>
    [ObservableProperty] private bool _bypassOpensPage;
    [ObservableProperty] private bool _isProxyRunning;
    [ObservableProperty] private HomeBanner? _banner;
    /// <summary>A short confirmation of something that needs no decision («Ссылка скопирована»).</summary>
    [ObservableProperty] private string? _notice;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ConnectCommand), nameof(DisconnectCommand), nameof(NewSecretCommand), nameof(RunBannerActionCommand))]
    private bool _isBusy;

    /// <param name="createQr">A new «QR для телефона» window's view-model, made when it is asked for (it reads the settings then).</param>
    public HomeViewModel(IProxyControl proxy, IControlClient control, StatusMonitor monitor, IAppSettings settings,
                         ILauncher launcher, IClipboardService clipboard, IDialogService dialogs, INavigator navigator,
                         Func<QrViewModel> createQr)
        : base(AppPageInfo.Title(AppPage.Home), AppPageInfo.Icon(AppPage.Home))
    {
        _createQr = createQr;
        _proxy = proxy;
        _control = control;
        _monitor = monitor;
        _settings = settings;
        _launcher = launcher;
        _clipboard = clipboard;
        _dialogs = dialogs;
        _navigator = navigator;

        Apply(monitor.Current);
        monitor.Changed += Apply;
    }

    private bool CanAct() => !IsBusy;

    private void Apply(AppSnapshot s)
    {
        (Headline, HeadlineTone) = StatusText.Headline(s);
        (BypassLine, BypassTone) = StatusText.BypassLine(s);
        BypassOpensPage = s.ServiceReachable;
        IsProxyRunning = s.Proxy.Running;
        (ProxyLine, ProxyTone) = StatusText.ProxyLine(s.Proxy, _settings.Load().Host.ToString());
    }

    /// <summary>Starts the proxy and, when the bypass service is there but off, turns it on too.</summary>
    [RelayCommand(CanExecute = nameof(CanAct))]
    private async Task ConnectAsync()
    {
        await Busy(async () =>
        {
            if (!await StartProxyAsync()) return;

            await _monitor.RefreshNowAsync();
            if (_monitor.Current is { ServiceReachable: true, Bypass: { State: "disabled" } })
            {
                try
                {
                    ControlResponse r = await _control.SendAsync(new ControlRequest(ControlCommands.On));
                    if (!r.Ok) Banner = new HomeBanner($"Обход DPI не включился: {r.Error}", Tone.Red);
                }
                catch (ServiceUnavailableException)
                {
                    // it went away since the poll: the bypass line will say so
                }
                await _monitor.RefreshNowAsync();
            }
        });
    }

    /// <summary>Stops this user's proxy. The bypass is shared with everyone on the machine and is left alone.</summary>
    [RelayCommand(CanExecute = nameof(CanAct))]
    private async Task DisconnectAsync()
    {
        await Busy(async () =>
        {
            try
            {
                await _proxy.StopAsync();
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                Banner = new HomeBanner($"Не удалось остановить прокси: {ex.Message}", Tone.Red);
            }
            await _monitor.RefreshNowAsync();
        });
    }

    [RelayCommand]
    private async Task OpenInTelegramAsync()
    {
        string link = _settings.TelegramLink();
        if (await _launcher.OpenAsync(link)) return;
        Banner = new HomeBanner("Не удалось открыть Telegram. Скопируйте ссылку и вставьте её в Telegram вручную.", Tone.Amber, BannerAction.CopyLink);
    }

    [RelayCommand]
    private async Task CopyLinkAsync()
    {
        await _clipboard.SetTextAsync(_settings.TelegramLink());
        Notice = "Ссылка скопирована";
    }

    /// <summary>The code for a phone on this network: only there is one to show while sharing is on, so otherwise this goes to the setting.</summary>
    [RelayCommand]
    private async Task ShowQrAsync()
    {
        if (!_settings.LanSharing)
        {
            _navigator.Show(AppPage.Settings);
            return;
        }
        await _dialogs.ShowQrAsync(_createQr());
    }

    /// <summary>A new secret: the Telegram entry made from the old one stops working, so this asks first, and offers the restart that makes it take effect.</summary>
    [RelayCommand(CanExecute = nameof(CanAct))]
    private async Task NewSecretAsync()
    {
        if (!await _dialogs.ConfirmAsync("Новый секрет",
                "Прежняя запись прокси в Telegram перестанет работать: её придётся добавить заново по новой ссылке. Продолжить?",
                "Создать", "Отмена"))
            return;

        await Busy(async () =>
        {
            _settings.ReplaceSecret();
            Notice = "Создан новый секрет";
            if (!_proxy.RestartNeeded()) return;

            if (await _dialogs.ConfirmAsync("Перезапустить прокси?", "Чтобы новый секрет заработал, прокси нужно перезапустить.", "Перезапустить", "Позже"))
                await RestartCoreAsync();
            else
                Banner = new HomeBanner("Прокси работает со старым секретом — перезапустите его.", Tone.Amber, BannerAction.Restart);
        });
    }

    [RelayCommand(CanExecute = nameof(CanAct))]
    private async Task RunBannerActionAsync()
    {
        switch (Banner?.Action)
        {
            case BannerAction.OpenInTelegram:
                await OpenInTelegramAsync();
                break;
            case BannerAction.CopyLink:
                await CopyLinkAsync();
                break;
            case BannerAction.Restart:
                await Busy(RestartCoreAsync);
                break;
        }
    }

    /// <summary>Stops the proxy and starts it again, for a change (settings, a new secret) that only a restart puts into effect.</summary>
    public Task RestartProxyAsync() => Busy(RestartCoreAsync);

    [RelayCommand]
    private void DismissBanner() => Banner = null;

    [RelayCommand]
    private void CheckConnection() => _navigator.Show(AppPage.Diagnostics, startCheck: true);

    [RelayCommand]
    private void OpenBypass() => _navigator.Show(AppPage.Bypass);

    private async Task RestartCoreAsync()
    {
        try
        {
            await _proxy.StopAsync();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Banner = new HomeBanner($"Не удалось остановить прокси: {ex.Message}", Tone.Red);
            return;
        }
        await StartProxyAsync();
        await _monitor.RefreshNowAsync();
    }

    /// <summary>Starts the proxy and reports it. A different port than asked for gets its banner; any failure shows in the headline and the banner.</summary>
    /// <returns>True when the proxy is up.</returns>
    private async Task<bool> StartProxyAsync()
    {
        StartResult r;
        try
        {
            r = await _proxy.StartAsync();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Another start held the lock too long, or the files could not be written: no result to remember.
            Banner = new HomeBanner($"Не удалось запустить прокси: {ex.Message}", Tone.Red);
            return false;
        }

        _monitor.RecordStart(r);
        Banner = r.Outcome switch
        {
            StartOutcome.PortSwitched => new HomeBanner(
                $"Порт {r.OldPort} занят — прокси на {r.Port}. Откройте ссылку в Telegram заново.", Tone.Amber, BannerAction.OpenInTelegram),
            _ when !r.Ok => new HomeBanner(FailureDetails(r), Tone.Red),
            _ => null,
        };
        return r.Ok;
    }

    private static string FailureDetails(StartResult r)
    {
        string first = r.Output.Split('\n', 2)[0].Trim();
        string reason = StatusText.StartFailure(r);
        return first.Length == 0 ? $"Не удалось запустить прокси: {reason}." : $"Не удалось запустить прокси: {reason}. Движок сообщил: {first}";
    }

    // Everything that changes something runs one at a time: the buttons are off while it does, and a fresh start clears the old message.
    private async Task Busy(Func<Task> work)
    {
        IsBusy = true;
        Banner = null;
        Notice = null;
        try
        {
            await work();
        }
        finally
        {
            IsBusy = false;
        }
    }
}
