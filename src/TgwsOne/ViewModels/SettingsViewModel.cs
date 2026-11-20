using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using TgwsOne.Core;
using TgwsOne.Services;

namespace TgwsOne.ViewModels;

/// <summary>Настройки: the proxy's settings as a form (errors under their fields, nothing saved while any remains), sharing with a phone, autostart, the secret.</summary>
internal sealed partial class SettingsViewModel : PageViewModel
{
    public const string MaskedSecret = "••••••••••••";
    public const string NoAddressText = "Не нашёл адрес в этой сети: подключитесь к Wi-Fi или кабелю.";

    private readonly IAppSettings _settings;
    private readonly IProxyControl _proxy;
    private readonly IDialogService _dialogs;
    private readonly IClipboardService _clipboard;
    private readonly INetInfo _net;
    private readonly IAutostart _autostart;
    private readonly Func<Task> _restartProxy;

    private bool _loading;
    private string? _hostBeforeSharing;
    private string _secret = "";

    // ----- the form -----
    [ObservableProperty] private string _port = "";
    [ObservableProperty] private string _host = "";
    [ObservableProperty] private string _dcMap = "";
    [ObservableProperty] private bool _cfProxy;
    [ObservableProperty] private bool _h2;
    [ObservableProperty] private bool _noSecure;
    [ObservableProperty] private bool _verbose;
    [ObservableProperty] private string _cfDomains = "";
    [ObservableProperty] private string _workerDomains = "";
    [ObservableProperty] private string _bufKb = "";
    [ObservableProperty] private string _poolSize = "";
    [ObservableProperty] private string _logMaxMb = "";

    // ----- what is wrong with it, per field -----
    [ObservableProperty] private string? _portError;
    [ObservableProperty] private string? _hostError;
    [ObservableProperty] private string? _dcMapError;
    [ObservableProperty] private string? _bufKbError;
    [ObservableProperty] private string? _poolSizeError;
    [ObservableProperty] private string? _logMaxMbError;

    // ----- sharing, autostart, secret -----
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HostEnabled), nameof(ShowNoAddress))]
    private bool _lanSharing;

    [ObservableProperty] private LanAddress? _selectedLanAddress;
    [ObservableProperty] private bool _autostartOn;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SecretText), nameof(ShowSecretText))]
    private bool _secretShown;

    [ObservableProperty] private string? _notice;
    [ObservableProperty] private bool _isAdvancedOpen;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SaveCommand), nameof(NewSecretCommand))]
    private bool _isBusy;

    public SettingsViewModel(IAppSettings settings, IProxyControl proxy, IDialogService dialogs, IClipboardService clipboard,
                             INetInfo net, IAutostart autostart, Func<Task> restartProxy)
        : base(AppPageInfo.Title(AppPage.Settings), AppPageInfo.Icon(AppPage.Settings))
    {
        _settings = settings;
        _proxy = proxy;
        _dialogs = dialogs;
        _clipboard = clipboard;
        _net = net;
        _autostart = autostart;
        _restartProxy = restartProxy;
        Reload();
    }

    public ObservableCollection<LanAddress> LanAddresses { get; } = [];

    /// <summary>The Host field is the proxy's own business while sharing is on: it listens on every interface.</summary>
    public bool HostEnabled => !LanSharing;

    /// <summary>Sharing is on, but no address a phone could use was found.</summary>
    public bool ShowNoAddress => LanSharing && LanAddresses.Count == 0;

    public string SecretText => SecretShown ? _secret : MaskedSecret;

    public bool ShowSecretText => SecretShown;

    public override void OnShown() => Reload();

    // ----- loading -----

    /// <summary>Reads everything again from where it lives, dropping what was typed and not saved.</summary>
    public void Reload()
    {
        _loading = true;
        try
        {
            ProxyForm f = _settings.ReadForm();
            Port = f.Port;
            Host = f.Host;
            DcMap = f.DcMap;
            CfProxy = f.CfProxy;
            H2 = f.H2;
            NoSecure = f.NoSecure;
            Verbose = f.Verbose;
            CfDomains = f.CfDomains;
            WorkerDomains = f.WorkerDomains;
            BufKb = f.BufKb;
            PoolSize = f.PoolSize;
            LogMaxMb = f.LogMaxMb;
            ClearErrors();

            LanSharing = _settings.LanSharing;
            _hostBeforeSharing = LanSharing ? _settings.PreviousHost : null;
            SelectedLanAddress = null;   // what was picked and not saved is dropped with the rest: the saved address is looked for
            RefreshLanAddresses();

            AutostartOn = _autostart.IsEnabled();
            _secret = _settings.Secret();
            SecretShown = false;
            OnPropertyChanged(nameof(SecretText));
            Notice = null;
        }
        finally
        {
            _loading = false;
        }
    }

    /// <summary>Looks at the network again: which addresses a phone could use. The one picked stays picked while it exists; otherwise the saved one, otherwise the first.</summary>
    public void RefreshLanAddresses()
    {
        string? wanted = SelectedLanAddress?.Address.ToString() ?? _settings.LanAddress;
        LanAddresses.Clear();
        foreach (LanAddress a in LanShare.Candidates(_net)) LanAddresses.Add(a);
        SelectedLanAddress = LanAddresses.FirstOrDefault(a => a.Address.ToString() == wanted) ?? LanAddresses.FirstOrDefault();
        OnPropertyChanged(nameof(ShowNoAddress));
    }

    // ----- sharing -----

    partial void OnLanSharingChanged(bool value)
    {
        if (_loading) return;
        if (value)
        {
            if (Host != LanShare.WildcardHost) _hostBeforeSharing = Host;
            Host = LanShare.WildcardHost;
            HostError = null;
            RefreshLanAddresses();
        }
        else
        {
            Host = _hostBeforeSharing ?? _settings.PreviousHost;
        }
    }

    // ----- autostart: at once, it needs no restart -----

    partial void OnAutostartOnChanged(bool value)
    {
        if (_loading) return;
        try
        {
            _autostart.Set(value);
            Notice = null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _loading = true;   // putting the box back must not try again
            AutostartOn = !value;
            _loading = false;
            Notice = $"Не удалось изменить автозапуск: {ex.Message}";
        }
    }

    // ----- saving -----

    private bool CanAct() => !IsBusy;

    [RelayCommand(CanExecute = nameof(CanAct))]
    private async Task SaveAsync()
    {
        IsBusy = true;
        try
        {
            ClearErrors();
            Notice = null;
            string host = LanSharing ? LanShare.WildcardHost : Host;
            var form = new ProxyForm(host, Port, DcMap, CfProxy, H2, NoSecure, Verbose, CfDomains, WorkerDomains, BufKb, PoolSize, LogMaxMb);
            (ProxySettings? parsed, IReadOnlyList<FieldError> errors) = ProxySettingsStore.Parse(form, _settings.Secret());
            if (parsed is null)
            {
                ShowErrors(errors);
                return;
            }

            _settings.Save(parsed, LanSharing, LanSharing ? SelectedLanAddress?.Address.ToString() : null, LanSharing ? _hostBeforeSharing : null);
            Notice = "Сохранено";

            if (!_proxy.RestartNeeded()) return;
            if (await _dialogs.ConfirmAsync("Настройки сохранены", "Перезапустить прокси сейчас, чтобы настройки применились?", "Перезапустить", "Позже"))
            {
                await _restartProxy();
                Notice = "Сохранено, прокси перезапущен";
            }
            else
            {
                Notice = "Сохранено. Прокси применит настройки после перезапуска.";
            }
        }
        finally
        {
            IsBusy = false;
        }
    }

    private void ClearErrors()
    {
        PortError = HostError = DcMapError = BufKbError = PoolSizeError = LogMaxMbError = null;
    }

    private void ShowErrors(IReadOnlyList<FieldError> errors)
    {
        foreach (FieldError e in errors)
        {
            switch (e.Field)
            {
                case nameof(ProxyForm.Port): PortError = e.Message; break;
                case nameof(ProxyForm.Host): HostError = e.Message; break;
                case nameof(ProxyForm.DcMap): DcMapError = e.Message; break;
                case nameof(ProxyForm.BufKb): BufKbError = e.Message; break;
                case nameof(ProxyForm.PoolSize): PoolSizeError = e.Message; break;
                case nameof(ProxyForm.LogMaxMb): LogMaxMbError = e.Message; break;
            }
        }
    }

    // ----- the secret and the link -----

    [RelayCommand]
    private void ToggleSecret() => SecretShown = !SecretShown;

    [RelayCommand]
    private async Task CopyLinkAsync()
    {
        await _clipboard.SetTextAsync(_settings.TelegramLink());
        Notice = "Ссылка скопирована";
    }

    [RelayCommand(CanExecute = nameof(CanAct))]
    private async Task NewSecretAsync()
    {
        if (!await _dialogs.ConfirmAsync("Новый секрет",
                "Прежняя запись прокси в Telegram перестанет работать: её придётся добавить заново по новой ссылке. Продолжить?",
                "Создать", "Отмена"))
            return;

        IsBusy = true;
        try
        {
            _settings.ReplaceSecret();
            _secret = _settings.Secret();
            OnPropertyChanged(nameof(SecretText));
            Notice = "Создан новый секрет";
            if (_proxy.RestartNeeded()
                && await _dialogs.ConfirmAsync("Перезапустить прокси?", "Чтобы новый секрет заработал, прокси нужно перезапустить.", "Перезапустить", "Позже"))
            {
                await _restartProxy();
                Notice = "Создан новый секрет, прокси перезапущен";
            }
        }
        finally
        {
            IsBusy = false;
        }
    }
}
