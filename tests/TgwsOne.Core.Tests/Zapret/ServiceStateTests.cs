using TgwsOne.Core.Zapret;

namespace TgwsOne.Core.Tests.Zapret;

public class ServiceStateTests
{
    [Fact]
    public void MissingFile_GivesTheDefaults()
    {
        using var tmp = new TempDir();

        Assert.Equal(new ServiceState(true, "auto"), ServiceState.Load(tmp.Combine("state.ini")));
    }

    [Fact]
    public void RoundTrip()
    {
        using var tmp = new TempDir();
        string ini = tmp.Combine("sub", "state.ini");

        new ServiceState(false, "split").Save(ini);

        Assert.Equal(new ServiceState(false, "split"), ServiceState.Load(ini));
        Assert.Equal("[zapret]\nenabled=0\nstrategy=split\nscope=telegram\n", File.ReadAllText(ini).Replace("\r\n", "\n"));

        new ServiceState(true, "custom:mine").Save(ini);
        Assert.Equal(new ServiceState(true, "custom:mine"), ServiceState.Load(ini));
    }

    [Theory]
    [InlineData("scope=all", ZapretScope.All)]
    [InlineData("scope=ALL", ZapretScope.All)]
    [InlineData("scope=telegram", ZapretScope.Telegram)]
    [InlineData("scope=junk", ZapretScope.Telegram)]
    [InlineData("", ZapretScope.Telegram)]
    public void Scope_Parsing(string line, ZapretScope expected)
    {
        using var tmp = new TempDir();
        string ini = tmp.Combine("state.ini");
        File.WriteAllText(ini, $"[zapret]\n{line}\n");

        // A missing or unknown key must never widen the bypass: everything but "all" is the narrow scope.
        Assert.Equal(expected, ServiceState.Load(ini).Scope);
    }

    [Fact]
    public void UnknownStrategy_IsKeptAsIs()
    {
        using var tmp = new TempDir();
        string ini = tmp.Combine("state.ini");
        File.WriteAllText(ini, "[zapret]\nenabled=1\nstrategy=from-the-future\n");

        Assert.Equal("from-the-future", ServiceState.Load(ini).Strategy);
    }

    [Theory]
    [InlineData("enabled=0", false)]
    [InlineData("enabled=1", true)]
    [InlineData("enabled=false", false)]
    [InlineData("enabled=TRUE", true)]
    [InlineData("enabled=", true)]
    [InlineData("enabled=junk", true)]
    [InlineData("", true)]
    public void Enabled_Parsing(string line, bool expected)
    {
        using var tmp = new TempDir();
        string ini = tmp.Combine("state.ini");
        File.WriteAllText(ini, $"[zapret]\n{line}\nstrategy=split\n");

        Assert.Equal(expected, ServiceState.Load(ini).Enabled);
    }

    [Fact]
    public void BlankStrategy_FallsBackToAuto()
    {
        using var tmp = new TempDir();
        string ini = tmp.Combine("state.ini");
        File.WriteAllText(ini, "[zapret]\nstrategy=   \n");

        Assert.Equal("auto", ServiceState.Load(ini).Strategy);
    }

    [Fact]
    public void Save_KeepsOtherSections()
    {
        using var tmp = new TempDir();
        string ini = tmp.Combine("state.ini");
        File.WriteAllText(ini, "[other]\nkeep=me\n[zapret]\nenabled=1\nstrategy=auto\n");

        new ServiceState(false, "aggressive").Save(ini);

        var loaded = IniFile.Load(ini);
        Assert.Equal("me", loaded.Get("other", "keep"));
        Assert.Equal("0", loaded.Get("zapret", "enabled"));
        Assert.Equal("aggressive", loaded.Get("zapret", "strategy"));
    }
}
