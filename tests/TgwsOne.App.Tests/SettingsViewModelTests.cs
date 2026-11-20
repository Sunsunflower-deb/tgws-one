using System.Net;
using TgwsOne.Core;
using TgwsOne.Services;
using TgwsOne.ViewModels;

namespace TgwsOne.Tests;

public class SettingsViewModelTests
{
    private sealed class Rig
    {
        public FakeAppSettings Settings { get; } = new();
        public FakeProxyControl Proxy { get; } = new();
        public FakeDialogs Dialogs { get; } = new();
        public FakeClipboard Clipboard { get; } = new();
        public FakeNetInfo Net { get; } = new();
        public FakeAutostart Autostart { get; } = new();
        public int Restarts { get; private set; }
        public SettingsViewModel Vm { get; private set; } = null!;

        public Rig(Action<Rig>? arrange = null)
        {
            Net.SetInterfaces(new NetInterfaceInfo("enp5s0", "Network adapter", true, NetKind.Ethernet,
                [IPAddress.Parse("192.168.18.20")], [IPAddress.Parse("192.168.18.1")]));
            arrange?.Invoke(this);
            Vm = new SettingsViewModel(Settings, Proxy, Dialogs, Clipboard, Net, Autostart, () =>
            {
                Restarts++;
                return Task.CompletedTask;
            });
        }
    }

    // ----- loading -----

    [Fact]
    public void ItStartsFromTheSavedSettings()
    {
        var rig = new Rig(r => r.Settings.Form = new ProxyForm("127.0.0.2", "2443", "4:1.2.3.4", false, false, true, false, "a.example.com", "w.example.com", "512", "8", "10"));

        SettingsViewModel vm = rig.Vm;

        Assert.Equal(("127.0.0.2", "2443", "4:1.2.3.4"), (vm.Host, vm.Port, vm.DcMap));
        Assert.Equal((false, false, true, false), (vm.CfProxy, vm.H2, vm.NoSecure, vm.Verbose));
        Assert.Equal(("a.example.com", "w.example.com", "512", "8", "10"), (vm.CfDomains, vm.WorkerDomains, vm.BufKb, vm.PoolSize, vm.LogMaxMb));
        Assert.Equal("Настройки", vm.Title);
    }

    [Fact]
    public void ShowingThePage_DropsWhatWasTypedAndNotSaved()
    {
        var rig = new Rig();
        rig.Vm.Port = "9999";
        rig.Vm.PortError = "что-то";

        rig.Vm.OnShown();

        Assert.Equal("1443", rig.Vm.Port);
        Assert.Null(rig.Vm.PortError);
    }

    // ----- validation -----

    [Fact]
    public async Task EveryFieldsError_AppearsUnderThatField()
    {
        var rig = new Rig();
        rig.Vm.Port = "abc";
        rig.Vm.Host = "not-an-address";
        rig.Vm.DcMap = "oops";
        rig.Vm.BufKb = "1";
        rig.Vm.PoolSize = "-3";
        rig.Vm.LogMaxMb = "0";

        await rig.Vm.SaveCommand.ExecuteAsync(null);

        Assert.Equal("Порт должен быть числом от 1 до 65535.", rig.Vm.PortError);
        Assert.Equal("Хост должен быть IP-адресом, например 127.0.0.1.", rig.Vm.HostError);
        Assert.StartsWith("Строка DC → IP должна быть в формате номер:IP", rig.Vm.DcMapError);
        Assert.Equal("Буфер — не меньше 4 КБ.", rig.Vm.BufKbError);
        Assert.Equal("Пул WS — не меньше 0.", rig.Vm.PoolSizeError);
        Assert.Equal("Размер лога — не меньше 1 МБ.", rig.Vm.LogMaxMbError);
    }

    [Fact]
    public async Task WhileAnyErrorRemains_NothingIsSaved()
    {
        var rig = new Rig();
        rig.Vm.Port = "70000";   // one bad field among good ones

        await rig.Vm.SaveCommand.ExecuteAsync(null);

        Assert.Equal(0, rig.Settings.Saves);
        Assert.Null(rig.Settings.Saved);
        Assert.NotNull(rig.Vm.PortError);
        Assert.Null(rig.Vm.HostError);   // the good ones have no message
        Assert.Null(rig.Vm.Notice);
        Assert.Empty(rig.Dialogs.Asked);
    }

    [Fact]
    public async Task FixingTheField_ClearsTheError_AndSaves()
    {
        var rig = new Rig();
        rig.Vm.Port = "abc";
        await rig.Vm.SaveCommand.ExecuteAsync(null);
        Assert.NotNull(rig.Vm.PortError);

        rig.Vm.Port = "2443";
        await rig.Vm.SaveCommand.ExecuteAsync(null);

        Assert.Null(rig.Vm.PortError);
        Assert.Equal(1, rig.Settings.Saves);
        Assert.Equal(2443, rig.Settings.Saved!.Value.Settings.Port);
        Assert.Equal("Сохранено", rig.Vm.Notice);
    }

    [Fact]
    public async Task ASuccessfulSave_SendsEverythingTheFormHolds()
    {
        var rig = new Rig();
        rig.Vm.Port = "2443";
        rig.Vm.BufKb = "512";
        rig.Vm.PoolSize = "8";
        rig.Vm.LogMaxMb = "10";
        rig.Vm.Verbose = false;
        rig.Vm.CfDomains = "a.example.com, b.example.com";

        await rig.Vm.SaveCommand.ExecuteAsync(null);

        ProxySettings s = rig.Settings.Saved!.Value.Settings;
        Assert.Equal((2443, 512, 8, 10, false), (s.Port, s.BufKb, s.PoolSize, s.LogMaxMb, s.Verbose));
        Assert.Equal(["a.example.com", "b.example.com"], s.CfDomains);
        Assert.Equal(FakeAppSettings.InitialSecret, s.Secret);
        Assert.Equal("127.0.0.1", s.Host.ToString());
    }

    // ----- the restart question -----

    [Fact]
    public async Task NoRestartQuestion_WhenNothingNeedsOne()
    {
        var rig = new Rig();
        rig.Proxy.RestartIsNeeded = false;

        await rig.Vm.SaveCommand.ExecuteAsync(null);

        Assert.Empty(rig.Dialogs.Asked);
        Assert.Equal(0, rig.Restarts);
        Assert.Equal("Сохранено", rig.Vm.Notice);
    }

    [Fact]
    public async Task WhenARestartIsNeeded_ItAsks_AndRestartsOnYes()
    {
        var rig = new Rig();
        rig.Proxy.RestartIsNeeded = true;
        rig.Dialogs.Answers.Enqueue(true);

        await rig.Vm.SaveCommand.ExecuteAsync(null);

        Assert.Equal(["Настройки сохранены"], rig.Dialogs.Asked);
        Assert.Equal(1, rig.Restarts);
        Assert.Equal("Сохранено, прокси перезапущен", rig.Vm.Notice);
    }

    [Fact]
    public async Task WhenARestartIsNeeded_AndTheUserSaysLater_NothingIsRestarted()
    {
        var rig = new Rig();
        rig.Proxy.RestartIsNeeded = true;
        rig.Dialogs.Answers.Enqueue(false);

        await rig.Vm.SaveCommand.ExecuteAsync(null);

        Assert.Equal(0, rig.Restarts);
        Assert.Equal(1, rig.Settings.Saves);   // saved all the same
        Assert.Equal("Сохранено. Прокси применит настройки после перезапуска.", rig.Vm.Notice);
    }

    [Fact]
    public async Task NoRestartQuestion_WhileThereAreErrors()
    {
        var rig = new Rig();
        rig.Proxy.RestartIsNeeded = true;
        rig.Vm.Port = "abc";

        await rig.Vm.SaveCommand.ExecuteAsync(null);

        Assert.Empty(rig.Dialogs.Asked);
    }

    // ----- sharing with a phone -----

    [Fact]
    public void TurningSharingOn_FillsTheHostWithTheWildcard_AndDisablesTheField()
    {
        var rig = new Rig();
        rig.Vm.Host = "127.0.0.2";

        rig.Vm.LanSharing = true;

        Assert.Equal("0.0.0.0", rig.Vm.Host);
        Assert.False(rig.Vm.HostEnabled);
    }

    [Fact]
    public void TurningSharingOff_PutsTheHostBack()
    {
        var rig = new Rig();
        rig.Vm.Host = "127.0.0.2";
        rig.Vm.LanSharing = true;

        rig.Vm.LanSharing = false;

        Assert.Equal("127.0.0.2", rig.Vm.Host);
        Assert.True(rig.Vm.HostEnabled);
    }

    [Fact]
    public void WhenSharingWasAlreadyOn_TheHostBackIsTheRememberedOne()
    {
        var rig = new Rig(r =>
        {
            r.Settings.LanSharing = true;
            r.Settings.PreviousHost = "127.0.0.9";
            r.Settings.Form = r.Settings.Form with { Host = "0.0.0.0" };
        });
        Assert.Equal("0.0.0.0", rig.Vm.Host);
        Assert.True(rig.Vm.LanSharing);

        rig.Vm.LanSharing = false;

        Assert.Equal("127.0.0.9", rig.Vm.Host);
    }

    [Fact]
    public async Task SavingWithSharing_SendsTheSwitch_TheAddress_AndTheHostToGoBackTo()
    {
        var rig = new Rig();
        rig.Vm.Host = "127.0.0.2";
        rig.Vm.LanSharing = true;

        await rig.Vm.SaveCommand.ExecuteAsync(null);

        var saved = rig.Settings.Saved!.Value;
        Assert.True(saved.LanSharing);
        Assert.Equal("192.168.18.20", saved.LanAddress);
        Assert.Equal("0.0.0.0", saved.Settings.Host.ToString());
        Assert.Equal("127.0.0.2", rig.Settings.SavedPreviousHost);
    }

    [Fact]
    public async Task SavingWithoutSharing_SendsNoAddress()
    {
        var rig = new Rig();

        await rig.Vm.SaveCommand.ExecuteAsync(null);

        var saved = rig.Settings.Saved!.Value;
        Assert.False(saved.LanSharing);
        Assert.Null(saved.LanAddress);
        Assert.Null(rig.Settings.SavedPreviousHost);
    }

    [Fact]
    public async Task WhileSharing_ABadHostInTheDisabledField_IsNotAnError()
    {
        var rig = new Rig();
        rig.Vm.Host = "garbage";
        rig.Vm.LanSharing = true;   // the field now reads 0.0.0.0 and is not editable

        await rig.Vm.SaveCommand.ExecuteAsync(null);

        Assert.Null(rig.Vm.HostError);
        Assert.Equal(1, rig.Settings.Saves);
    }

    [Fact]
    public void TheAddressesAreTheLanCandidates_AndTheSavedOneIsChosen()
    {
        var rig = new Rig(r =>
        {
            r.Net.SetInterfaces(
                new NetInterfaceInfo("enp5s0", "Network adapter", true, NetKind.Ethernet, [IPAddress.Parse("192.168.18.20")], [IPAddress.Parse("192.168.18.1")]),
                new NetInterfaceInfo("wlp2s0", "Network adapter", true, NetKind.Wireless, [IPAddress.Parse("10.1.1.9")], [IPAddress.Parse("10.1.1.1")]),
                new NetInterfaceInfo("tailscale0", "Network adapter", true, NetKind.Tunnel, [IPAddress.Parse("100.64.0.5")], [IPAddress.Parse("100.64.0.1")]));
            r.Settings.LanAddress = "10.1.1.9";
        });

        Assert.Equal(["192.168.18.20 (enp5s0)", "10.1.1.9 (wlp2s0)"], rig.Vm.LanAddresses.Select(a => a.ToString()));
        Assert.Equal("10.1.1.9", rig.Vm.SelectedLanAddress!.Address.ToString());
    }

    [Fact]
    public void WithoutTheSavedAddress_TheFirstOneIsChosen()
    {
        var rig = new Rig(r => r.Settings.LanAddress = "172.16.0.9");   // gone since it was saved

        Assert.Equal("192.168.18.20", rig.Vm.SelectedLanAddress!.Address.ToString());
    }

    [Fact]
    public void TheChoiceMade_SurvivesATurnOfTheSwitch()
    {
        var rig = new Rig(r => r.Net.SetInterfaces(
            new NetInterfaceInfo("enp5s0", "Network adapter", true, NetKind.Ethernet, [IPAddress.Parse("192.168.18.20")], [IPAddress.Parse("192.168.18.1")]),
            new NetInterfaceInfo("wlp2s0", "Network adapter", true, NetKind.Wireless, [IPAddress.Parse("10.1.1.9")], [IPAddress.Parse("10.1.1.1")])));
        rig.Vm.SelectedLanAddress = rig.Vm.LanAddresses[1];

        rig.Vm.LanSharing = true;

        Assert.Equal("10.1.1.9", rig.Vm.SelectedLanAddress!.Address.ToString());
    }

    [Fact]
    public void WithNoAddressAtAll_SharingSaysSo()
    {
        var rig = new Rig(r => r.Net.SetInterfaces());
        Assert.False(rig.Vm.ShowNoAddress);   // not while sharing is off

        rig.Vm.LanSharing = true;

        Assert.True(rig.Vm.ShowNoAddress);
        Assert.Null(rig.Vm.SelectedLanAddress);
        Assert.Contains("Wi-Fi", SettingsViewModel.NoAddressText);
    }

    [Fact]
    public async Task SavingWithSharingAndNoAddress_IsAllowed_WithNoAddress()
    {
        var rig = new Rig(r => r.Net.SetInterfaces());
        rig.Vm.LanSharing = true;

        await rig.Vm.SaveCommand.ExecuteAsync(null);

        Assert.Null(rig.Settings.Saved!.Value.LanAddress);
        Assert.True(rig.Settings.Saved.Value.LanSharing);
    }

    // ----- autostart -----

    [Fact]
    public void TheBoxShowsWhetherAutostartIsOn()
    {
        Assert.False(new Rig().Vm.AutostartOn);
        Assert.True(new Rig(r => r.Autostart.Enabled = true).Vm.AutostartOn);
    }

    [Fact]
    public void TickingTheBox_AppliesItAtOnce_WithoutSaving()
    {
        var rig = new Rig();

        rig.Vm.AutostartOn = true;
        rig.Vm.AutostartOn = false;

        Assert.Equal([true, false], rig.Autostart.Calls);
        Assert.Equal(0, rig.Settings.Saves);
    }

    [Fact]
    public void LoadingThePage_DoesNotWriteAnything()
    {
        var rig = new Rig(r => r.Autostart.Enabled = true);

        rig.Vm.OnShown();

        Assert.Empty(rig.Autostart.Calls);   // reading the state must not set it
    }

    [Fact]
    public void AutostartThatCannotBeWritten_PutsTheBoxBack_AndSaysWhy()
    {
        var rig = new Rig(r => r.Autostart.Throws = new IOException("профиль только для чтения"));

        rig.Vm.AutostartOn = true;

        Assert.False(rig.Vm.AutostartOn);
        Assert.Equal("Не удалось изменить автозапуск: профиль только для чтения", rig.Vm.Notice);
        Assert.Equal([true], rig.Autostart.Calls);   // tried once, and the revert did not try again
    }

    // ----- the secret -----

    [Fact]
    public void TheSecret_IsHiddenAtFirst_AndShownOnRequest()
    {
        var rig = new Rig();

        Assert.Equal("••••••••••••", rig.Vm.SecretText);
        Assert.False(rig.Vm.ShowSecretText);

        rig.Vm.ToggleSecretCommand.Execute(null);
        Assert.Equal(FakeAppSettings.InitialSecret, rig.Vm.SecretText);
        Assert.True(rig.Vm.ShowSecretText);

        rig.Vm.ToggleSecretCommand.Execute(null);
        Assert.Equal("••••••••••••", rig.Vm.SecretText);
    }

    [Fact]
    public async Task CopyLink_PutsTheLinkOnTheClipboard()
    {
        var rig = new Rig();

        await rig.Vm.CopyLinkCommand.ExecuteAsync(null);

        Assert.Equal(rig.Settings.Link, rig.Clipboard.Text);
        Assert.Equal("Ссылка скопирована", rig.Vm.Notice);
    }

    [Fact]
    public async Task NewSecret_Declined_ChangesNothing()
    {
        var rig = new Rig();
        rig.Dialogs.Answers.Enqueue(false);

        await rig.Vm.NewSecretCommand.ExecuteAsync(null);

        Assert.Equal(0, rig.Settings.SecretsReplaced);
        Assert.Equal(["Новый секрет"], rig.Dialogs.Asked);
    }

    [Fact]
    public async Task NewSecret_ReplacesIt_AndTheShownValueFollows()
    {
        var rig = new Rig();
        rig.Vm.ToggleSecretCommand.Execute(null);
        string before = rig.Vm.SecretText;

        await rig.Vm.NewSecretCommand.ExecuteAsync(null);

        Assert.Equal(1, rig.Settings.SecretsReplaced);
        Assert.NotEqual(before, rig.Vm.SecretText);
        Assert.Equal(rig.Settings.CurrentSecret, rig.Vm.SecretText);
        Assert.Equal("Создан новый секрет", rig.Vm.Notice);
        Assert.Equal(["Новый секрет"], rig.Dialogs.Asked);   // no running proxy: no restart to offer
    }

    [Fact]
    public async Task NewSecret_WithARunningProxy_OffersTheRestart()
    {
        var rig = new Rig();
        rig.Proxy.RestartIsNeeded = true;

        await rig.Vm.NewSecretCommand.ExecuteAsync(null);

        Assert.Equal(["Новый секрет", "Перезапустить прокси?"], rig.Dialogs.Asked);
        Assert.Equal(1, rig.Restarts);
        Assert.Equal("Создан новый секрет, прокси перезапущен", rig.Vm.Notice);
    }

    [Fact]
    public async Task NewSecret_ARestartLater_LeavesTheProxyAlone()
    {
        var rig = new Rig();
        rig.Proxy.RestartIsNeeded = true;
        rig.Dialogs.Answers.Enqueue(true);
        rig.Dialogs.Answers.Enqueue(false);

        await rig.Vm.NewSecretCommand.ExecuteAsync(null);

        Assert.Equal(0, rig.Restarts);
    }
}
