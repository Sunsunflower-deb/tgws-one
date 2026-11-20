using System.Text.RegularExpressions;
using TgwsOne.Core;
using TgwsOne.Services;

namespace TgwsOne.Tests;

public class IniAppSettingsTests
{
    private static AppPaths PathsIn(TempDir tmp)
        => AppPaths.For(OsKind.Linux, n => n == "TGWS_DATA_DIR" ? tmp.Path : null, appDir: "/opt/tgwsone", home: "/home/nobody");

    [Fact]
    public void TelegramLink_MakesTheSecretFirst_AndKeepsIt()
    {
        using var tmp = new TempDir();
        var settings = new IniAppSettings(PathsIn(tmp));

        string first = settings.TelegramLink();
        string second = settings.TelegramLink();

        Assert.Matches(new Regex(@"^tg://proxy\?server=127\.0\.0\.1&port=1443&secret=dd[0-9a-f]{32}$"), first);
        Assert.Equal(first, second);
        Assert.Equal(first[^32..], IniFile.Load(PathsIn(tmp).Ini).Get("app", "secret"));
    }

    [Fact]
    public void TelegramLink_FollowsAPortTheProxyMovedTo()
    {
        // A start that found the port busy rewrites the INI; the link must be made from what is there now, never from a copy.
        using var tmp = new TempDir();
        var settings = new IniAppSettings(PathsIn(tmp));
        Assert.Contains("port=1443", settings.TelegramLink());

        IniFile ini = IniFile.Load(PathsIn(tmp).Ini);
        ini.Set("proxy", "port", "1444");
        ini.Save();

        Assert.Contains("port=1444", settings.TelegramLink());
        Assert.Equal(1444, settings.Load().Port);
    }

    [Fact]
    public void TelegramLink_ForAWildcardListenAddress_PointsAtThisMachine()
    {
        using var tmp = new TempDir();
        IniFile ini = IniFile.Load(PathsIn(tmp).Ini);
        ini.Set("proxy", "host", "0.0.0.0");
        ini.Save();

        Assert.Contains("server=127.0.0.1", new IniAppSettings(PathsIn(tmp)).TelegramLink());
    }

    [Fact]
    public void ReplaceSecret_WritesANewValidSecret_AndTheLinkChanges()
    {
        using var tmp = new TempDir();
        var settings = new IniAppSettings(PathsIn(tmp));
        string before = settings.TelegramLink();

        settings.ReplaceSecret();
        string after = settings.TelegramLink();

        Assert.NotEqual(before[^32..], after[^32..]);
        Assert.True(ProxySettingsStore.IsValidSecret(after[^32..]));
        Assert.Equal(after[^32..], IniFile.Load(PathsIn(tmp).Ini).Get("app", "secret"));
    }

    [Fact]
    public void Load_WithoutAFile_GivesTheDefaults()
    {
        using var tmp = new TempDir();

        ProxySettings s = new IniAppSettings(PathsIn(tmp)).Load();

        Assert.Equal(1443, s.Port);
        Assert.Equal("127.0.0.1", s.Host.ToString());
    }
}
