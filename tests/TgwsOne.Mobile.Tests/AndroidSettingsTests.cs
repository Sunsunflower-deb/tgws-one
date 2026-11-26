using TgwsOne.Core;

namespace TgwsOne.Mobile.Tests;

public class AndroidSettingsTests
{
    [Fact]
    public void Load_WithoutAFile_GivesTheDefaults()
    {
        using var temp = new TempFiles();

        AndroidSettings settings = AndroidSettingsStore.Load(IniFile.Load(temp.Storage.IniPath));

        Assert.Equal(("auto", "", 180), (settings.Strategy, settings.Provider, settings.PoolIdle));
    }

    [Fact]
    public void Save_ThenLoad_RoundTripsTheSection()
    {
        using var temp = new TempFiles();
        var wanted = new AndroidSettings("split-sni", "mts", 60);

        AndroidSettingsStore.Save(IniFile.Load(temp.Storage.IniPath), wanted);
        AndroidSettings loaded = AndroidSettingsStore.Load(IniFile.Load(temp.Storage.IniPath));

        Assert.Equal(wanted, loaded);
        Assert.Equal("split-sni", IniFile.Load(temp.Storage.IniPath).Get("android", "strategy"));
    }

    [Theory]
    [InlineData("soon")]
    [InlineData("-5")]
    [InlineData("")]
    public void GarbagePoolIdle_FallsBackToTheDefault(string raw)
    {
        using var temp = new TempFiles();
        temp.Configure(ini => ini.Set("android", "poolidle", raw));

        Assert.Equal(AndroidSettings.DefaultPoolIdle, AndroidSettingsStore.Load(IniFile.Load(temp.Storage.IniPath)).PoolIdle);
    }

    [Fact]
    public void GarbageStrategyAndProvider_FallBackToTheDefaults()
    {
        using var temp = new TempFiles();
        temp.Configure(ini =>
        {
            ini.Set("android", "strategy", "no-such-strategy");
            ini.Set("android", "provider", "Not A Provider");
        });

        AndroidSettings settings = AndroidSettingsStore.Load(IniFile.Load(temp.Storage.IniPath));

        Assert.Equal(AndroidSettings.DefaultStrategy, settings.Strategy);
        Assert.Equal(AndroidSettings.DefaultProvider, settings.Provider);
    }

    [Fact]
    public void ZeroPoolIdle_IsARealValue_NotAbsent()
    {
        using var temp = new TempFiles();
        temp.Configure(ini => ini.Set("android", "poolidle", "0"));

        Assert.Equal(0, AndroidSettingsStore.Load(IniFile.Load(temp.Storage.IniPath)).PoolIdle);
    }

    [Fact]
    public void Save_KeepsTheSecretTheDesktopStoreMade()
    {
        using var temp = new TempFiles();
        IniFile ini = IniFile.Load(temp.Storage.IniPath);
        string secret = ProxySettingsStore.EnsureSecret(ini);

        AndroidSettingsStore.Save(ini, new AndroidSettings("fake", "mts", 30));

        Assert.Equal(secret, IniFile.Load(temp.Storage.IniPath).Get("app", "secret"));
    }
}
