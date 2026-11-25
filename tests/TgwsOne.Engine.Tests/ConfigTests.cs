using System.Net;
using TgwsOne.Engine.Transport;

namespace TgwsOne.Engine.Tests;

public class EngineConfigParserTests
{
    private const string Secret = "0123456789abcdef0123456789abcdef";

    private static EngineConfig Parse(params string[] args)
    {
        Assert.Null(EngineConfigParser.TryParse(args, out EngineConfig config));
        return config;
    }

    [Fact]
    public void Defaults_MatchTheHistoricalEngine()
    {
        EngineConfig c = Parse("--secret", Secret);

        Assert.Equal("127.0.0.1", c.Host);
        Assert.Equal(1443, c.Port);
        Assert.Equal(Secret, c.Secret);
        Assert.Equal(2, c.DcRedirects.Count);
        Assert.Equal("149.154.167.220", c.DcRedirects[2]);
        Assert.Equal("149.154.167.220", c.DcRedirects[4]);
        Assert.True(c.FallbackCfProxy);
        Assert.True(c.CfproxyH2Media);
        Assert.True(c.H2Enabled);
        Assert.False(c.DisableSecure);
        Assert.Equal(256 * 1024, c.BufferSize);
        Assert.Equal(4, c.PoolSize);
        Assert.Null(c.LogFile);
        Assert.Equal(5, c.LogMaxMb);
        Assert.Equal(1, c.LogBackups);
    }

    [Fact]
    public void NoSecret_IsGeneratedAs32HexCharacters()
    {
        EngineConfig c = Parse();
        Assert.True(EngineConfig.IsValidSecret(c.Secret));
    }

    [Fact]
    public void BareDcIp_MeansAnEmptyList_NotTheDefaults()
    {
        EngineConfig c = Parse("--secret", Secret, "--dc-ip");
        Assert.Empty(c.DcRedirects);
    }

    [Fact]
    public void DcIp_RepeatsAndParses()
    {
        EngineConfig c = Parse("--secret", Secret, "--dc-ip", "2:149.154.167.1", "--dc-ip", "4:149.154.167.2");
        Assert.Equal("149.154.167.1", c.DcRedirects[2]);
        Assert.Equal("149.154.167.2", c.DcRedirects[4]);
    }

    [Fact]
    public void BadDcIp_IsAnError()
    {
        Assert.NotNull(EngineConfigParser.TryParse(["--dc-ip", "2:not-an-ip"], out _));
        Assert.NotNull(EngineConfigParser.TryParse(["--dc-ip", "x:149.154.167.1"], out _));
        Assert.NotNull(EngineConfigParser.TryParse(["--dc-ip", "2:2001:db8::1"], out _));   // upstream accepts IPv4 only
    }

    [Fact]
    public void Secret_MustBe32HexCharacters()
    {
        Assert.NotNull(EngineConfigParser.TryParse(["--secret", "abc"], out _));
        Assert.NotNull(EngineConfigParser.TryParse(["--secret", new string('z', 32)], out _));
        Assert.Null(EngineConfigParser.TryParse(["--secret", Secret.ToUpperInvariant()], out _));
    }

    [Fact]
    public void Flags_MapToTheSameBooleansAsUpstream()
    {
        EngineConfig c = Parse("--secret", Secret, "--no-cfproxy", "--no-h2", "--no-secure",
            "--fake-tls-domain", "example.test", "--force-test-dc", "--proxy-protocol", "-v");

        Assert.False(c.FallbackCfProxy);
        Assert.False(c.CfproxyH2Media);
        Assert.False(c.H2Enabled);
        Assert.True(c.DisableSecure);
        Assert.Equal("example.test", c.FakeTlsDomain);
        Assert.True(c.ForceTestDc);
        Assert.True(c.ProxyProtocol);
        Assert.True(c.Verbose);
    }

    [Fact]
    public void H2_IsOffWhereTheCfRouteItselfIsUnusable()
    {
        Assert.False(Parse("--secret", Secret, "--no-secure").H2Enabled);
        Assert.False(Parse("--secret", Secret, "--force-test-dc").H2Enabled);
        Assert.False(Parse("--secret", Secret, "--no-cfproxy").H2Enabled);
    }

    [Fact]
    public void Domains_AreSplitAndDeduplicatedCaseInsensitively()
    {
        EngineConfig c = Parse("--secret", Secret,
            "--cfproxy-domain", "a.com,b.com", "--cfproxy-domain", "A.COM c.com",
            "--cfproxy-worker-domain", "w.dev;x.dev");

        Assert.Equal(["a.com", "b.com", "c.com"], c.CfproxyUserDomains);
        Assert.Equal(["w.dev", "x.dev"], c.CfproxyWorkerDomains);
    }

    [Fact]
    public void NumbersAndLogFlags_AreRead()
    {
        EngineConfig c = Parse("--secret", Secret, "--host", "0.0.0.0", "--port", "2080",
            "--buf-kb", "64", "--pool-size", "0", "--log-file", "/tmp/p.log", "--log-max-mb", "9.5",
            "--log-backups", "3");

        Assert.Equal("0.0.0.0", c.Host);
        Assert.Equal(2080, c.Port);
        Assert.Equal(64 * 1024, c.BufferSize);
        Assert.Equal(0, c.PoolSize);
        Assert.Equal("/tmp/p.log", c.LogFile);
        Assert.Equal(9.5, c.LogMaxMb);
        Assert.Equal(3, c.LogBackups);
    }

    [Theory]
    [InlineData("--port")]
    [InlineData("--unknown")]
    [InlineData("stray")]
    public void BadArguments_AreErrors(string arg)
    {
        Assert.NotNull(EngineConfigParser.TryParse(["--secret", Secret, arg], out _));
    }

    [Theory]
    [InlineData("--host", "localhost")]     // a name is not an address: the engine has nothing to bind to
    [InlineData("--host", "127.0.0.1:1443")]
    [InlineData("--host", "")]
    [InlineData("--port", "0")]
    [InlineData("--port", "65536")]
    [InlineData("--port", "-1")]
    public void AHostThatIsNotAnAddress_AndAPortThatIsNotOne_AreRejectedByTheParser(string flag, string value)
    {
        // Rejected here, with the other argument errors; otherwise the engine starts and dies with a library message.
        Assert.NotNull(EngineConfigParser.TryParse(["--secret", Secret, flag, value], out _));
    }

    [Theory]
    [InlineData("127.0.0.1")]
    [InlineData("0.0.0.0")]
    [InlineData("::1")]
    public void AnyIpAddress_IsAHost(string host)
        => Assert.Equal(host, Parse("--secret", Secret, "--host", host).Host);

    [Theory]
    [InlineData("1")]
    [InlineData("65535")]
    public void BothEndsOfThePortRange_AreAccepted(string port)
        => Assert.Equal(int.Parse(port), Parse("--secret", Secret, "--port", port).Port);

    [Fact]
    public void Help_IsRecognised()
    {
        Assert.Equal("help", EngineConfigParser.TryParse(["--help"], out _));
    }

    [Fact]
    public void TelegramLink_UsesTheConnectAddress()
    {
        var c = Parse("--secret", Secret, "--host", "0.0.0.0", "--port", "2080");
        // 0.0.0.0 becomes the machine's outbound address; only the stable parts are asserted.
        Assert.StartsWith("tg://proxy?server=", c.TelegramLink());
        Assert.Contains("&port=2080&secret=dd" + Secret, c.TelegramLink());
        Assert.Equal("tg://proxy?server=127.0.0.1&port=2080&secret=dd" + Secret,
            Parse("--secret", Secret, "--port", "2080").TelegramLink());
    }
}

public class DomainBalancerTests
{
    [Fact]
    public void DecodesTheUpstreamDefaultListExactly()
    {
        Assert.Equal(
        [
            "pclead.co.uk", "offshor.co.uk", "cakeisalie.co.uk", "noskomnadzor.co.uk", "lovetrue.co.uk",
            "sorokdva.co.uk", "pyatdesyatdva.co.uk", "kartoshka.co.uk", "sorokodin.co.uk", "pyatdesyatodin.co.uk",
            "notelega.co.uk", "ebally.co.uk", "nebally.co.uk", "havegreatday.co.uk", "pomogite.co.uk",
            "fixtelega.co.uk", "sadnews.co.uk", "onedaychamp.co.uk", "stopblocking.co.uk", "nothingthere.co.uk",
        ], DomainBalancer.DefaultDomains);
    }

    [Fact]
    public void PreferredDomainComesFirst_ThenAllTheOthersOnce()
    {
        var balancer = new DomainBalancer();
        balancer.UpdateDomainsList(["a.test", "b.test", "c.test", "d.test"]);
        balancer.UpdateDomainForDc(2, "c.test");

        var domains = balancer.GetDomainsForDc(2);

        Assert.Equal("c.test", domains[0]);
        Assert.Equal(4, domains.Count);
        Assert.Equal(domains.Distinct().Count(), domains.Count);
        Assert.Contains("a.test", domains);
    }

    [Fact]
    public void UpdateWithTheSameSet_KeepsTheChosenDomain()
    {
        var balancer = new DomainBalancer();
        balancer.UpdateDomainsList(["a.test", "b.test"]);
        balancer.UpdateDomainForDc(2, "b.test");
        balancer.UpdateDomainsList(["b.test", "a.test"]);
        Assert.Equal("b.test", balancer.GetDomainsForDc(2)[0]);
    }

    [Theory]
    [InlineData("a.test", true)]
    [InlineData("kws2.pclead.co.uk", true)]
    [InlineData("a", false)]
    [InlineData(".a.test", false)]
    [InlineData("a..test", false)]
    [InlineData("-a.test", false)]
    [InlineData("a-.test", false)]
    [InlineData("a.t1", true)]
    [InlineData("a.b_c", false)]
    public void DomainValidation_FollowsUpstream(string domain, bool valid)
        => Assert.Equal(valid, CfDomainRefresher.IsValidDomain(domain));
}
