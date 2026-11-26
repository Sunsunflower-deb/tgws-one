using TgwsOne.Engine.Desync;

namespace TgwsOne.Mobile.Tests;

public class SettingsViewModelTests
{
    private sealed class Rig
    {
        public FakeMobileSettings Settings { get; } = new();
        public SettingsViewModel Vm { get; }

        public Rig()
        {
            Vm = new SettingsViewModel(Settings);
        }

        public void Choose(string id) => Vm.SelectedStrategy = Vm.Strategies.Single(choice => choice.Id == id);
    }

    [Fact]
    public void ItStartsWithTheDefaults_AndEveryBuiltInStrategy()
    {
        var rig = new Rig();

        Assert.Equal(AndroidSettings.DefaultStrategy, rig.Vm.SelectedStrategy.Id);
        Assert.Equal("Авто", rig.Vm.SelectedStrategy.Name);
        Assert.Equal("", rig.Vm.Provider);
        Assert.Equal("180", rig.Vm.PoolIdle);
        Assert.Equal([AndroidSettings.DefaultStrategy, .. BuiltInDesync.Ids], rig.Vm.Strategies.Select(choice => choice.Id));
        Assert.All(rig.Vm.Strategies, choice => Assert.Contains(choice.Name, text => text is >= 'А' and <= 'я'));
    }

    [Fact]
    public void ItReadsWhatTheAndroidSectionHolds()
    {
        var rig = new Rig();
        rig.Settings.Android = new AndroidSettings("split-sni", "mts", 60);

        rig.Vm.Reload();

        Assert.Equal("split-sni", rig.Vm.SelectedStrategy.Id);
        Assert.Equal("Разрез после имени", rig.Vm.SelectedStrategy.Name);
        Assert.Equal("mts", rig.Vm.Provider);
        Assert.Equal("60", rig.Vm.PoolIdle);
    }

    [Fact]
    public void Save_WritesTheAndroidSection()
    {
        var rig = new Rig();
        rig.Choose("fake");
        rig.Vm.Provider = "mts";
        rig.Vm.PoolIdle = "30";

        rig.Vm.SaveCommand.Execute(null);

        Assert.Equal(new AndroidSettings("fake", "mts", 30), rig.Settings.Android);
        Assert.Equal(1, rig.Settings.Saves);
        Assert.Equal("Сохранено", rig.Vm.Notice);
        Assert.Null(rig.Vm.ProviderError);
        Assert.Null(rig.Vm.PoolIdleError);
    }

    [Fact]
    public void AnEmptyProvider_MeansAutomatic()
    {
        var rig = new Rig();
        rig.Vm.Provider = "";

        rig.Vm.SaveCommand.Execute(null);

        Assert.Equal("", rig.Settings.Android.Provider);
        Assert.Equal(1, rig.Settings.Saves);
    }

    [Theory]
    [InlineData("soon")]
    [InlineData("-5")]
    [InlineData("")]
    public void AnInvalidPoolIdle_StopsTheSave(string raw)
    {
        var rig = new Rig();
        rig.Vm.PoolIdle = raw;

        rig.Vm.SaveCommand.Execute(null);

        Assert.Equal("Пауза пула — целое число секунд, не меньше 0.", rig.Vm.PoolIdleError);
        Assert.Equal(0, rig.Settings.Saves);
        Assert.Null(rig.Vm.Notice);
    }

    [Theory]
    [InlineData("Not A Provider")]
    [InlineData("MTS")]
    [InlineData("-bad")]
    public void AnInvalidProvider_StopsTheSave(string provider)
    {
        var rig = new Rig();
        rig.Vm.Provider = provider;

        rig.Vm.SaveCommand.Execute(null);

        Assert.StartsWith("Провайдер — имя вида mts:", rig.Vm.ProviderError);
        Assert.Equal(0, rig.Settings.Saves);
    }

    [Fact]
    public void Reload_DropsWhatWasTypedButNotSaved()
    {
        var rig = new Rig();
        rig.Vm.Provider = "мтс";
        rig.Vm.PoolIdle = "1";

        rig.Vm.Reload();

        Assert.Equal("", rig.Vm.Provider);
        Assert.Equal("180", rig.Vm.PoolIdle);
        Assert.Equal(AndroidSettings.DefaultStrategy, rig.Vm.SelectedStrategy.Id);
    }
}
