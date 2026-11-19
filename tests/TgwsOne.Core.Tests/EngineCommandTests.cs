using System.Net;

namespace TgwsOne.Core.Tests;

public class EngineCommandTests
{
    private const string Secret = "0123456789abcdef0123456789abcdef";

    // Linux layout is deterministic on any host: the proxy log lives in /home/u/.local/state/tgwsone/logs.
    private static readonly AppPaths Paths = AppPaths.For(OsKind.Linux, _ => null, "/opt/app", "/home/u");
    private const string ProxyLog = "/home/u/.local/state/tgwsone/logs/proxy.log";

    private static ProxySettings Defaults() => new(
        Host: IPAddress.Parse("127.0.0.1"), Port: 1443,
        DcMap: [new DcEntry(2, IPAddress.Parse("149.154.167.220")), new DcEntry(4, IPAddress.Parse("149.154.167.220"))],
        CfProxy: true, H2: true, NoSecure: false, Verbose: true,
        CfDomains: [], WorkerDomains: [], BufKb: 256, PoolSize: 4, LogMaxMb: 5, Secret: Secret);

    [Fact]
    public void Build_Defaults_ExactArgv()
    {
        Assert.Equal(
        [
            "--host", "127.0.0.1", "--port", "1443", "--secret", Secret,
            "--dc-ip", "2:149.154.167.220", "--dc-ip", "4:149.154.167.220",
            "-v",
            "--log-file", ProxyLog, "--log-max-mb", "5", "--log-backups", "1",
            "--buf-kb", "256", "--pool-size", "4",
        ], EngineCommand.Build(Defaults(), Paths));
    }

    [Fact]
    public void Build_EmptyDcMap_BareDcIp()
    {
        // Without a bare --dc-ip the engine falls back to its built-in DC 2/4 list.
        var argv = EngineCommand.Build(Defaults() with { DcMap = [] }, Paths);

        Assert.Equal(["--host", "127.0.0.1", "--port", "1443", "--secret", Secret, "--dc-ip", "-v"], argv.Take(8));
        Assert.Single(argv, a => a == "--dc-ip");
    }

    [Fact]
    public void Build_TogglesOff()
    {
        var argv = EngineCommand.Build(Defaults() with { CfProxy = false, H2 = false, NoSecure = true, Verbose = false }, Paths);

        Assert.Equal(
        [
            "--host", "127.0.0.1", "--port", "1443", "--secret", Secret,
            "--dc-ip", "2:149.154.167.220", "--dc-ip", "4:149.154.167.220",
            "--no-cfproxy", "--no-h2", "--no-secure",
            "--log-file", ProxyLog, "--log-max-mb", "5", "--log-backups", "1",
            "--buf-kb", "256", "--pool-size", "4",
        ], argv);
    }

    [Fact]
    public void Build_RepeatsDomainFlags()
    {
        var argv = EngineCommand.Build(Defaults() with { CfDomains = ["a.com", "b.com"], WorkerDomains = ["w.dev"] }, Paths);

        Assert.Equal(
        [
            "--cfproxy-domain", "a.com", "--cfproxy-domain", "b.com", "--cfproxy-worker-domain", "w.dev", "-v",
        ], argv.Skip(10).Take(7));
    }

    [Fact]
    public void Build_UsesSettingsValues()
    {
        var argv = EngineCommand.Build(Defaults() with { Host = IPAddress.Parse("::1"), Port = 2080, BufKb = 64, PoolSize = 0, LogMaxMb = 9 }, Paths);

        Assert.Equal(["--host", "::1", "--port", "2080"], argv.Take(4));
        Assert.Equal(["--log-max-mb", "9", "--log-backups", "1", "--buf-kb", "64", "--pool-size", "0"], argv.TakeLast(8));
    }

    [Fact]
    public void Hash_ChangesWithSecret()
    {
        var a = EngineCommand.Hash(EngineCommand.Build(Defaults(), Paths));
        var b = EngineCommand.Hash(EngineCommand.Build(Defaults() with { Secret = new string('f', 32) }, Paths));

        Assert.NotEqual(a, b);
    }

    [Fact]
    public void Hash_StableForSameInput()
    {
        var a = EngineCommand.Hash(EngineCommand.Build(Defaults(), Paths));
        var b = EngineCommand.Hash(EngineCommand.Build(Defaults(), Paths));

        Assert.Equal(a, b);
        Assert.Matches("^[0-9a-f]{64}$", a);
    }

    [Fact]
    public void Hash_DistinguishesArgumentBoundaries()
    {
        // ["ab","c"] and ["a","bc"] must not collide (the separator is NUL, not nothing).
        Assert.NotEqual(EngineCommand.Hash(["ab", "c"]), EngineCommand.Hash(["a", "bc"]));
    }

    [Fact]
    public void Mask_HidesSecret()
    {
        string shown = EngineCommand.Mask(EngineCommand.Build(Defaults(), Paths));

        Assert.DoesNotContain(Secret, shown);
        Assert.Contains("--secret ********", shown);
        Assert.StartsWith("--host 127.0.0.1 --port 1443 --secret ******** --dc-ip 2:149.154.167.220", shown);
    }

    [Fact]
    public void Mask_QuotesArgumentsWithSpaces()
    {
        string shown = EngineCommand.Mask(["--log-file", "/path with space/proxy.log", "--secret", "x"]);

        Assert.Equal("--log-file \"/path with space/proxy.log\" --secret ********", shown);
    }
}
