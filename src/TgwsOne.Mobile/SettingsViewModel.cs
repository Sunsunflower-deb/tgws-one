using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using TgwsOne.Engine.Desync;

namespace TgwsOne.Mobile;

/// <summary>One row of the strategy picker: the stored id and the Russian name shown for it.</summary>
public sealed record StrategyChoice(string Id, string Name);

/// <summary>
/// Настройки on the phone: the strategy, the operator whose rotation Авто walks, and how long an idle pool stays warm.
/// The values live in the <c>[android]</c> section; everything else about the proxy is left at its desktop defaults.
/// </summary>
public sealed partial class SettingsViewModel : ObservableObject
{
    private readonly IMobileSettings _settings;

    [ObservableProperty] private StrategyChoice _selectedStrategy;
    [ObservableProperty] private string _provider = "";
    [ObservableProperty] private string _poolIdle = "";
    [ObservableProperty] private string? _providerError;
    [ObservableProperty] private string? _poolIdleError;
    [ObservableProperty] private string? _notice;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SaveCommand))]
    private bool _isBusy;

    public SettingsViewModel(IMobileSettings settings)
    {
        _settings = settings;

        Strategies.Add(new StrategyChoice(AndroidSettings.DefaultStrategy, MobileStatusText.Auto));
        foreach (DesyncStrategy strategy in BuiltInDesync.BuiltIn)
            Strategies.Add(new StrategyChoice(strategy.Id, MobileStatusText.StrategyName(strategy.Id)));

        _selectedStrategy = Strategies[0];
        Reload();
    }

    public ObservableCollection<StrategyChoice> Strategies { get; } = [];

    public bool CanAct() => !IsBusy;

    /// <summary>Reads the Android section again, dropping what was typed and not saved.</summary>
    public void Reload()
    {
        AndroidSettings settings = _settings.LoadAndroid();
        SelectedStrategy = Strategies.FirstOrDefault(s => s.Id == settings.Strategy) ?? Strategies[0];
        Provider = settings.Provider;
        PoolIdle = settings.PoolIdle.ToString(CultureInfo.InvariantCulture);
        ProviderError = null;
        PoolIdleError = null;
        Notice = null;
    }

    [RelayCommand]
    private void ReloadSettings() => Reload();

    [RelayCommand(CanExecute = nameof(CanAct))]
    private void Save()
    {
        IsBusy = true;
        try
        {
            ProviderError = null;
            PoolIdleError = null;
            Notice = null;

            bool valid = true;
            string provider = Provider.Trim();
            if (provider.Length > 0 && !AndroidSettingsStore.IsValidProvider(provider))
            {
                ProviderError = "Провайдер — имя вида mts: строчные латинские буквы, цифры, точка, дефис или подчёркивание.";
                valid = false;
            }

            int poolIdle = 0;
            if (!int.TryParse(PoolIdle.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out poolIdle) || poolIdle < 0)
            {
                PoolIdleError = "Пауза пула — целое число секунд, не меньше 0.";
                valid = false;
            }
            if (!valid) return;

            _settings.SaveAndroid(new AndroidSettings(SelectedStrategy.Id, provider, poolIdle));
            Notice = "Сохранено";
        }
        finally
        {
            IsBusy = false;
        }
    }
}
