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

    // ----- the form and its saving -----

    private static ProxySettings Parsed(IAppSettings settings, Func<ProxyForm, ProxyForm>? change = null)
    {
        ProxyForm form = settings.ReadForm();
        (ProxySettings? parsed, IReadOnlyList<FieldError> errors) = ProxySettingsStore.Parse(change?.Invoke(form) ?? form, settings.Secret());
        Assert.Empty(errors);
        return parsed!;
    }

    [Fact]
    public void ReadForm_WithoutAFile_IsTheDefaultsOfTheEngine()
    {
        using var tmp = new TempDir();

        ProxyForm f = new IniAppSettings(PathsIn(tmp)).ReadForm();

        Assert.Equal(("127.0.0.1", "1443", "256", "4", "5"), (f.Host, f.Port, f.BufKb, f.PoolSize, f.LogMaxMb));
        Assert.True(f.CfProxy);
        Assert.True(f.H2);
        Assert.False(f.NoSecure);
        Assert.Equal("2:149.154.167.220\n4:149.154.167.220", f.DcMap);
    }

    [Fact]
    public void Secret_IsMadeOnce_AndKept()
    {
        using var tmp = new TempDir();
        var settings = new IniAppSettings(PathsIn(tmp));

        string first = settings.Secret();

        Assert.True(ProxySettingsStore.IsValidSecret(first));
        Assert.Equal(first, settings.Secret());
        Assert.Equal(first, new IniAppSettings(PathsIn(tmp)).Secret());   // on disk, not in memory
    }

    [Fact]
    public void Save_WritesTheSettings_AndKeepsTheSecret()
    {
        using var tmp = new TempDir();
        var settings = new IniAppSettings(PathsIn(tmp));
        string secret = settings.Secret();

        settings.Save(Parsed(settings, f => f with { Port = "2443", BufKb = "512", Verbose = false }), lanSharing: false, lanAddress: null);

        ProxyForm after = new IniAppSettings(PathsIn(tmp)).ReadForm();
        Assert.Equal(("2443", "512", false), (after.Port, after.BufKb, after.Verbose));
        Assert.Equal(secret, settings.Secret());
        Assert.False(settings.LanSharing);
    }

    [Fact]
    public void Save_WithSharing_ListensOnEveryInterface_AndRemembersTheHostAndTheAddress()
    {
        using var tmp = new TempDir();
        var settings = new IniAppSettings(PathsIn(tmp));

        settings.Save(Parsed(settings, f => f with { Host = "127.0.0.2" }), lanSharing: false, lanAddress: null);
        settings.Save(Parsed(settings), lanSharing: true, lanAddress: "192.168.18.20");

        var again = new IniAppSettings(PathsIn(tmp));
        Assert.True(again.LanSharing);
        Assert.Equal("0.0.0.0", again.ReadForm().Host);
        Assert.Equal("127.0.0.2", again.PreviousHost);
        Assert.Equal("192.168.18.20", again.LanAddress);
        Assert.Equal("0.0.0.0", again.Load().Host.ToString());
    }

    [Fact]
    public void Save_WithSharing_WhateverTheFormHostSays_IsTheWildcard()
    {
        using var tmp = new TempDir();
        var settings = new IniAppSettings(PathsIn(tmp));

        settings.Save(Parsed(settings, f => f with { Host = "192.168.1.5" }), lanSharing: true, lanAddress: "192.168.18.20");

        Assert.Equal("0.0.0.0", new IniAppSettings(PathsIn(tmp)).ReadForm().Host);
    }

    [Fact]
    public void Save_WithSharing_RemembersTheHostTheFormHad_NotTheOneThatWasInTheFile()
    {
        // The user typed a host and ticked sharing before saving: it is the typed one that must come back.
        using var tmp = new TempDir();
        var settings = new IniAppSettings(PathsIn(tmp));
        settings.Save(Parsed(settings), lanSharing: false, lanAddress: null);   // the file has the usual 127.0.0.1

        settings.Save(Parsed(settings), lanSharing: true, lanAddress: "192.168.18.20", previousHost: "127.0.0.5");

        Assert.Equal("127.0.0.5", new IniAppSettings(PathsIn(tmp)).PreviousHost);
    }

    [Fact]
    public void Save_WithSharing_NeverRemembersTheWildcardAsTheHostToGoBackTo()
    {
        using var tmp = new TempDir();
        var settings = new IniAppSettings(PathsIn(tmp));
        settings.Save(Parsed(settings, f => f with { Host = "127.0.0.2" }), lanSharing: false, lanAddress: null);
        settings.Save(Parsed(settings), lanSharing: true, lanAddress: "192.168.18.20");

        settings.Save(Parsed(settings), lanSharing: true, lanAddress: "192.168.18.21", previousHost: "0.0.0.0");   // already shared

        Assert.Equal("127.0.0.2", new IniAppSettings(PathsIn(tmp)).PreviousHost);
        Assert.Equal("192.168.18.21", new IniAppSettings(PathsIn(tmp)).LanAddress);
    }

    [Fact]
    public void Save_WithoutSharing_PutsTheRememberedHostBack_AndForgetsTheAddress()
    {
        using var tmp = new TempDir();
        var settings = new IniAppSettings(PathsIn(tmp));
        settings.Save(Parsed(settings, f => f with { Host = "127.0.0.2" }), lanSharing: false, lanAddress: null);
        settings.Save(Parsed(settings), lanSharing: true, lanAddress: "192.168.18.20");

        // The form now shows the remembered host, as the page does when sharing is switched off.
        settings.Save(Parsed(settings, f => f with { Host = settings.PreviousHost }), lanSharing: false, lanAddress: "ignored");

        var after = new IniAppSettings(PathsIn(tmp));
        Assert.False(after.LanSharing);
        Assert.Equal("127.0.0.2", after.ReadForm().Host);
        Assert.Null(after.LanAddress);
        Assert.Equal("127.0.0.1", after.PreviousHost);   // forgotten
    }

    [Fact]
    public void ReplaceSecret_ThenSave_KeepsTheNewSecret()
    {
        using var tmp = new TempDir();
        var settings = new IniAppSettings(PathsIn(tmp));
        string old = settings.Secret();
        settings.ReplaceSecret();
        string fresh = settings.Secret();

        settings.Save(Parsed(settings), lanSharing: false, lanAddress: null);

        Assert.NotEqual(old, fresh);
        Assert.Equal(fresh, new IniAppSettings(PathsIn(tmp)).Secret());
    }

    [Fact]
    public void FirstRun_IsNotDoneOnAFreshProfile_UntilMarked_AndIsRemembered()
    {
        using var tmp = new TempDir();
        var settings = new IniAppSettings(PathsIn(tmp));
        Assert.False(settings.FirstRunDone);

        settings.MarkFirstRunDone();

        Assert.True(settings.FirstRunDone);
        Assert.True(new IniAppSettings(PathsIn(tmp)).FirstRunDone);   // read back from the file
        Assert.Equal("done", IniFile.Load(PathsIn(tmp).Ini).Get("app", "firstrun"));
    }

    [Fact]
    public void MarkingTheFirstRun_KeepsTheOtherSettings_AndASaveDoesNotUndoIt()
    {
        using var tmp = new TempDir();
        var settings = new IniAppSettings(PathsIn(tmp));
        string secret = settings.Secret();
        settings.Save(Parsed(settings, f => f with { Port = "1555" }), lanSharing: false, lanAddress: null);

        settings.MarkFirstRunDone();
        settings.Save(Parsed(settings, f => f with { Port = "1556" }), lanSharing: false, lanAddress: null);

        var after = new IniAppSettings(PathsIn(tmp));
        Assert.True(after.FirstRunDone);
        Assert.Equal(secret, after.Secret());
        Assert.Equal(1556, after.Load().Port);
    }
}
