using TgwsOne.Core;
using TgwsOne.Core.Zapret;

namespace TgwsOne.Core.Tests;

/// <summary>The masking domain is what turns on the fake-TLS handshake, the <c>ee</c> link and the keep-alive pings.</summary>
public class FakeTlsSettingsTests
{
    private static ProxyForm FormWith(string? fakeTlsDomain)
    {
        ProxyForm form = ProxySettingsStore.ReadForm(IniFile.Load("/nonexistent/tgwsone.ini"));
        return fakeTlsDomain is null ? form : form with { FakeTlsDomain = fakeTlsDomain };
    }

    private static ProxySettings Parse(ProxyForm form)
    {
        (ProxySettings? settings, IReadOnlyList<FieldError> errors) = ProxySettingsStore.Parse(form, "00b42be1ae20f9e3b5d6e543cc4bce98");
        Assert.Empty(errors);
        return settings!;
    }

    [Fact]
    public void AnEmptyDomain_MeansNoFakeTls()
        => Assert.Equal("", Parse(FormWith("")).FakeTlsDomain);

    [Fact]
    public void ADomain_IsNormalised()
        => Assert.Equal("www.google.com", Parse(FormWith("  WWW.Google.COM.  ")).FakeTlsDomain);

    [Theory]
    [InlineData("www google com")]
    [InlineData("https://www.google.com")]
    [InlineData("google")]
    [InlineData("www.google.com/path")]
    [InlineData("-bad.example.com")]
    public void ABadDomain_IsAFieldError(string text)
    {
        (ProxySettings? settings, IReadOnlyList<FieldError> errors) = ProxySettingsStore.Parse(FormWith(text), "00b42be1ae20f9e3b5d6e543cc4bce98");

        Assert.Null(settings);
        Assert.Contains(errors, e => e.Field == nameof(ProxyForm.FakeTlsDomain));
    }

    [Fact]
    public void TheDomainSurvivesSaveAndLoad_UnderTheIniKeyFaketls()
    {
        using var tmp = new TempDir();
        string path = tmp.Combine("TgwsOne.ini");
        IniFile ini = IniFile.Load(path);
        ProxySettings parsed = Parse(FormWith("www.google.com"));

        ProxySettingsStore.Save(ini, parsed);
        string text = File.ReadAllText(path);
        ProxySettings loaded = ProxySettingsStore.Load(IniFile.Load(path));

        Assert.Contains("faketls=www.google.com", text.Replace("\r\n", "\n"));
        Assert.Equal("www.google.com", loaded.FakeTlsDomain);
    }

    [Fact]
    public void AnIniWithoutTheKey_GetsTheDefaultMaskingDomain()
    {
        using var tmp = new TempDir();
        string path = tmp.Combine("TgwsOne.ini");
        File.WriteAllText(path, "[proxy]\nport=1443\n[app]\nsecret=00b42be1ae20f9e3b5d6e543cc4bce98\n");

        Assert.Equal(ProxySettingsStore.DefaultFakeTlsDomain, ProxySettingsStore.Load(IniFile.Load(path)).FakeTlsDomain);
    }

    [Fact]
    public void AnExplicitlyEmptyValue_TurnsMaskingOff()
    {
        using var tmp = new TempDir();
        string path = tmp.Combine("TgwsOne.ini");
        File.WriteAllText(path, "[proxy]\nfaketls=\n[app]\nsecret=00b42be1ae20f9e3b5d6e543cc4bce98\n");

        Assert.Equal("", ProxySettingsStore.Load(IniFile.Load(path)).FakeTlsDomain);
    }

    [Fact]
    public void TheEngineGetsTheFlag_OnlyWhenADomainIsSet()
    {
        using var tmp = new TempDir();
        AppPaths paths = AppPaths.For(OsKind.Linux, _ => null, tmp.Path, tmp.Path);

        IReadOnlyList<string> withDomain = EngineCommand.Build(Parse(FormWith("www.google.com")), paths);
        IReadOnlyList<string> without = EngineCommand.Build(Parse(FormWith("")), paths);

        int at = withDomain.ToList().IndexOf("--fake-tls-domain");
        Assert.True(at >= 0);
        Assert.Equal("www.google.com", withDomain[at + 1]);
        Assert.DoesNotContain("--fake-tls-domain", without);
    }
}
