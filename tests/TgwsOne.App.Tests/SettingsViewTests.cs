using System.Net;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using TgwsOne.Services;
using TgwsOne.ViewModels;
using TgwsOne.Views;

namespace TgwsOne.Tests;

/// <summary>Настройки drawn on the headless platform.</summary>
public class SettingsViewTests
{
    private sealed class Rig
    {
        public FakeAppSettings Settings { get; } = new();
        public FakeNetInfo Net { get; } = new();
        public SettingsViewModel Vm { get; }
        public SettingsView View { get; }

        public Rig(bool withAddress = true)
        {
            if (withAddress)
                Net.SetInterfaces(new NetInterfaceInfo("enp5s0", "Network adapter", true, NetKind.Ethernet,
                    [IPAddress.Parse("192.168.18.20")], [IPAddress.Parse("192.168.18.1")]));
            Vm = new SettingsViewModel(Settings, new FakeProxyControl(), new FakeDialogs(), new FakeClipboard(), Net, new FakeAutostart(), () => Task.CompletedTask,
                                       () => new QrViewModel(Settings, Net, new FakeClipboard()));
            View = new SettingsView { DataContext = Vm };
            new Window { Content = View, Width = 820, Height = 900 }.Show();
            Dispatcher.UIThread.RunJobs();
        }

        public T Find<T>(string name) where T : Control
            => View.FindControl<T>(name) ?? throw new InvalidOperationException($"no control named {name}");

        public void Settle() => Dispatcher.UIThread.RunJobs();
    }

    [AvaloniaFact]
    public void TheSavedValues_AreInTheirBoxes()
    {
        var rig = new Rig();
        rig.Vm.IsAdvancedOpen = true;
        rig.Settle();

        Assert.Equal("1443", rig.Find<TextBox>("PortBox").Text);
        Assert.Equal("127.0.0.1", rig.Find<TextBox>("HostBox").Text);
        Assert.Equal("256", rig.Find<TextBox>("BufKbBox").Text);
        Assert.Equal("4", rig.Find<TextBox>("PoolSizeBox").Text);
        Assert.Equal("5", rig.Find<TextBox>("LogMaxMbBox").Text);
        Assert.Contains("2:149.154.167.220", rig.Find<TextBox>("DcMapBox").Text);
        Assert.False(rig.Find<CheckBox>("LanCheck").IsChecked);
        Assert.False(rig.Find<CheckBox>("AutostartCheck").IsChecked);
    }

    [AvaloniaFact]
    public void TheAdvancedPart_IsClosedAtFirst_AndOpensOnRequest()
    {
        var rig = new Rig();

        Assert.False(rig.Find<Expander>("AdvancedExpander").IsExpanded);
        rig.Vm.IsAdvancedOpen = true;
        rig.Settle();

        Assert.True(rig.Find<Expander>("AdvancedExpander").IsExpanded);
    }

    [AvaloniaFact]
    public async Task AnError_AppearsUnderItsOwnField_AndNowhereElse()
    {
        var rig = new Rig();
        rig.Vm.IsAdvancedOpen = true;
        rig.Find<TextBox>("PortBox").Text = "abc";   // typed: the binding carries it to the view-model
        rig.Find<TextBox>("BufKbBox").Text = "1";

        await rig.Vm.SaveCommand.ExecuteAsync(null);
        rig.Settle();

        Assert.True(rig.Find<TextBlock>("PortErrorText").IsVisible);
        Assert.Equal("Порт должен быть числом от 1 до 65535.", rig.Find<TextBlock>("PortErrorText").Text);
        Assert.True(rig.Find<TextBlock>("BufKbErrorText").IsVisible);
        Assert.Equal("Буфер — не меньше 4 КБ.", rig.Find<TextBlock>("BufKbErrorText").Text);
        Assert.False(rig.Find<TextBlock>("HostErrorText").IsVisible);
        Assert.False(rig.Find<TextBlock>("PoolSizeErrorText").IsVisible);
        Assert.False(rig.Find<TextBlock>("LogMaxMbErrorText").IsVisible);
        Assert.False(rig.Find<TextBlock>("DcMapErrorText").IsVisible);
        Assert.Null(rig.Settings.Saved);
    }

    [AvaloniaFact]
    public async Task AfterTheErrorIsFixed_TheMessageGoes_AndTheNoticeSaysSaved()
    {
        var rig = new Rig();
        rig.Find<TextBox>("PortBox").Text = "abc";
        await rig.Vm.SaveCommand.ExecuteAsync(null);
        rig.Settle();
        Assert.True(rig.Find<TextBlock>("PortErrorText").IsVisible);

        rig.Find<TextBox>("PortBox").Text = "2443";
        await rig.Vm.SaveCommand.ExecuteAsync(null);
        rig.Settle();

        Assert.False(rig.Find<TextBlock>("PortErrorText").IsVisible);
        Assert.True(rig.Find<TextBlock>("NoticeText").IsVisible);
        Assert.Equal("Сохранено", rig.Find<TextBlock>("NoticeText").Text);
    }

    [AvaloniaFact]
    public void Sharing_ShowsTheAddress_AndMakesTheHostReadOnly()
    {
        var rig = new Rig();
        rig.Vm.IsAdvancedOpen = true;
        Assert.False(rig.Find<StackPanel>("LanPanel").IsVisible);
        Assert.True(rig.Find<TextBox>("HostBox").IsEffectivelyEnabled);

        rig.Find<CheckBox>("LanCheck").IsChecked = true;
        rig.Settle();

        Assert.True(rig.Vm.LanSharing);
        Assert.True(rig.Find<StackPanel>("LanPanel").IsVisible);
        Assert.False(rig.Find<TextBox>("HostBox").IsEffectivelyEnabled);
        Assert.Equal("0.0.0.0", rig.Find<TextBox>("HostBox").Text);
        var box = rig.Find<ComboBox>("LanAddressBox");
        Assert.True(box.IsVisible);
        Assert.Equal(1, box.ItemCount);
        Assert.Equal("192.168.18.20 (enp5s0)", box.SelectedItem?.ToString());
        Assert.False(rig.Find<TextBlock>("NoAddressText").IsVisible);
    }

    [AvaloniaFact]
    public void SharingWithNoAddress_ShowsTheHintInsteadOfTheList()
    {
        var rig = new Rig(withAddress: false);

        rig.Find<CheckBox>("LanCheck").IsChecked = true;
        rig.Settle();

        Assert.False(rig.Find<ComboBox>("LanAddressBox").IsVisible);
        Assert.True(rig.Find<TextBlock>("NoAddressText").IsVisible);
        Assert.Equal(SettingsViewModel.NoAddressText, rig.Find<TextBlock>("NoAddressText").Text);
    }

    [AvaloniaFact]
    public void TheSecret_IsMasked_UntilShown()
    {
        var rig = new Rig();
        Assert.Equal("••••••••••••", rig.Find<TextBlock>("SecretValue").Text);

        rig.Find<Button>("ShowSecretButton").Command!.Execute(null);
        rig.Settle();

        Assert.Equal(FakeAppSettings.InitialSecret, rig.Find<TextBlock>("SecretValue").Text);
    }

    [AvaloniaFact]
    public void TheButtonsAreThere()
    {
        var rig = new Rig();

        Assert.Equal("Сохранить", rig.Find<Button>("SaveButton").Content);
        Assert.Equal("Показать", rig.Find<Button>("ShowSecretButton").Content);
        Assert.Equal("Новый…", rig.Find<Button>("NewSecretButton").Content);
        Assert.Equal("Копировать ссылку", rig.Find<Button>("CopyLinkButton").Content);
        Assert.Equal("Запускать при входе в систему", rig.Find<CheckBox>("AutostartCheck").Content);
        Assert.Equal("Разрешить подключение с телефона в этой сети", rig.Find<CheckBox>("LanCheck").Content);
    }
}
