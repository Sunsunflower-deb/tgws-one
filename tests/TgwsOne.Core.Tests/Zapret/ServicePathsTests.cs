using TgwsOne.Core.Zapret;

namespace TgwsOne.Core.Tests.Zapret;

public class ServicePathsTests
{
    private static Func<string, string?> Env(params (string Name, string Value)[] vars)
        => name => vars.FirstOrDefault(v => v.Name == name).Value;

    [Fact]
    public void Linux_DefaultLayout()
    {
        ServicePaths p = ServicePaths.For(OsKind.Linux, Env());

        Assert.Equal("/var/lib/tgwsone/state.ini", p.StateIni);
        Assert.Equal("/etc/tgwsone/lists", p.ListsDir);
        Assert.Equal("/var/lib/tgwsone", p.ExclusionsDir);
        Assert.Equal("/etc/tgwsone/strategies", p.StrategiesDir);
        Assert.Equal("/var/log/tgwsone", p.LogsDir);
        Assert.Equal("/var/lib/tgwsone", p.StateDir);
        Assert.Equal("/run/tgwsone/control.sock", p.Endpoint);
        Assert.Null(p.WindowsRoot);
        Assert.Equal(new ZapretLists(
            "/etc/tgwsone/lists/cloudflare-ips.txt", "/etc/tgwsone/lists/telegram.txt",
            "/var/lib/tgwsone/exclude-hosts.txt", "/var/lib/tgwsone/exclude-ips.txt"), p.Lists);
    }

    [Fact]
    public void Windows_DefaultLayout_KeepsThe1xListsFolder()
    {
        ServicePaths p = ServicePaths.For(OsKind.Windows, Env());

        Assert.Equal(@"C:\ProgramData\TgwsOne", p.WindowsRoot);
        Assert.Equal(@"C:\ProgramData\TgwsOne\service.ini", p.StateIni);
        Assert.Equal(@"C:\ProgramData\TgwsOne\lists", p.ListsDir);
        Assert.Equal(@"C:\ProgramData\TgwsOne\lists", p.ExclusionsDir);
        Assert.Equal(@"C:\ProgramData\TgwsOne\strategies", p.StrategiesDir);
        Assert.Equal(@"C:\ProgramData\TgwsOne\logs", p.LogsDir);
        Assert.Equal(@"C:\ProgramData\TgwsOne\state", p.StateDir);
        Assert.Equal("TgwsOne.Zapret", p.Endpoint);
        Assert.Equal(new ZapretLists(
            @"C:\ProgramData\TgwsOne\lists\cloudflare-ips.txt", @"C:\ProgramData\TgwsOne\lists\telegram.txt",
            @"C:\ProgramData\TgwsOne\lists\exclude-hosts.txt", @"C:\ProgramData\TgwsOne\lists\exclude-ips.txt"), p.Lists);
    }

    [Fact]
    public void Windows_ProgramDataFollowsTheEnvironment()
    {
        ServicePaths p = ServicePaths.For(OsKind.Windows, Env(("ProgramData", @"D:\Data")));

        Assert.Equal(@"D:\Data\TgwsOne", p.WindowsRoot);
        Assert.Equal(@"D:\Data\TgwsOne\service.ini", p.StateIni);
    }

    [Fact]
    public void Override_Linux_PutsEverythingUnderTheRoot()
    {
        ServicePaths p = ServicePaths.For(OsKind.Linux, Env(("TGWS_SERVICE_ROOT", "/tmp/x")));

        Assert.Equal("/tmp/x/state.ini", p.StateIni);
        Assert.Equal("/tmp/x/lists", p.ListsDir);
        Assert.Equal("/tmp/x/exclusions", p.ExclusionsDir);
        Assert.Equal("/tmp/x/strategies", p.StrategiesDir);
        Assert.Equal("/tmp/x/logs", p.LogsDir);
        Assert.Equal("/tmp/x/state", p.StateDir);
        Assert.Equal("/tmp/x/control.sock", p.Endpoint);
        Assert.Null(p.WindowsRoot);
        Assert.Equal("/tmp/x/exclusions/exclude-hosts.txt", p.Lists.ExcludeHosts);
        Assert.Equal("/tmp/x/lists/telegram.txt", p.Lists.TelegramHosts);
    }

    [Fact]
    public void Override_Windows_PipeNameGetsAHashSuffix()
    {
        ServicePaths a = ServicePaths.For(OsKind.Windows, Env(("TGWS_SERVICE_ROOT", @"C:\Temp\a")));
        ServicePaths again = ServicePaths.For(OsKind.Windows, Env(("TGWS_SERVICE_ROOT", @"C:\Temp\a")));
        ServicePaths b = ServicePaths.For(OsKind.Windows, Env(("TGWS_SERVICE_ROOT", @"C:\Temp\b")));

        Assert.Matches(@"^TgwsOne\.Zapret\.[0-9a-f]{8}$", a.Endpoint);
        Assert.Equal(a.Endpoint, again.Endpoint);
        Assert.NotEqual(a.Endpoint, b.Endpoint);
        Assert.Equal(@"C:\Temp\a", a.WindowsRoot);
        Assert.Equal(@"C:\Temp\a\state.ini", a.StateIni);
        Assert.Equal(@"C:\Temp\a\exclusions", a.ExclusionsDir);
    }

    [Fact]
    public void AutoStateFile_LivesInTheFolderNfqws2MakesWritable()
    {
        Assert.Equal("/var/lib/tgwsone/zapret-state/autostate.tsv", ServicePaths.For(OsKind.Linux, Env()).AutoStateFile);
        Assert.Equal(@"C:\ProgramData\TgwsOne\state\zapret-state\autostate.tsv", ServicePaths.For(OsKind.Windows, Env()).AutoStateFile);
        Assert.Equal("/tmp/x/state/zapret-state/autostate.tsv", ServicePaths.For(OsKind.Linux, Env(("TGWS_SERVICE_ROOT", "/tmp/x"))).AutoStateFile);

        // The argument builder points --writable at the very same folder.
        ServicePaths p = ServicePaths.For(OsKind.Linux, Env());
        var bundle = ZapretBundle.Locate(OsKind.Linux, "/opt/tgwsone", _ => null);
        string writable = ZapretArgs.Build(OsKind.Linux, bundle, p.Lists, "auto", p.StateDir).Single(a => a.StartsWith("--writable=", StringComparison.Ordinal));
        Assert.Equal(Path.GetDirectoryName(p.AutoStateFile), writable["--writable=".Length..]);
    }

    [Fact]
    public void EmptyOverride_IsIgnored()
        => Assert.Equal("/var/lib/tgwsone/state.ini", ServicePaths.For(OsKind.Linux, Env(("TGWS_SERVICE_ROOT", ""))).StateIni);

    [Fact]
    public void SeedTargetLists_CopiesOnlyTheMissingTargetLists()
    {
        using var tmp = new TempDir();
        string app = tmp.Combine("app-lists");
        Directory.CreateDirectory(app);
        File.WriteAllText(Path.Combine(app, "telegram.txt"), "shipped telegram\n");
        File.WriteAllText(Path.Combine(app, "cloudflare-ips.txt"), "shipped cloudflare\n");
        File.WriteAllText(Path.Combine(app, "exclude-hosts.txt"), "shipped exclusions\n");
        ServicePaths p = ServicePaths.For(OsKind.Linux, Env(("TGWS_SERVICE_ROOT", tmp.Combine("root"))));
        Directory.CreateDirectory(p.ListsDir);
        File.WriteAllText(p.Lists.TelegramHosts, "edited by the admin\n");

        p.SeedTargetLists(app);

        Assert.Equal("edited by the admin\n", File.ReadAllText(p.Lists.TelegramHosts));
        Assert.Equal("shipped cloudflare\n", File.ReadAllText(p.Lists.CloudflareIps));
        Assert.False(File.Exists(p.Lists.ExcludeHosts));
        Assert.False(File.Exists(Path.Combine(p.ListsDir, "exclude-hosts.txt")));
    }

    [Fact]
    public void SeedTargetLists_CreatesTheDirectory_AndToleratesAMissingSource()
    {
        using var tmp = new TempDir();
        ServicePaths p = ServicePaths.For(OsKind.Linux, Env(("TGWS_SERVICE_ROOT", tmp.Combine("root"))));

        p.SeedTargetLists(tmp.Combine("no-such-app-lists"));

        Assert.True(Directory.Exists(p.ListsDir));
        Assert.False(File.Exists(p.Lists.TelegramHosts));   // nothing to copy: the dry run will report the missing list
    }

    [Fact]
    public void EnsureExclusionFiles_CreatesThemWithAHeader_AndKeepsExistingOnes()
    {
        using var tmp = new TempDir();
        ServicePaths p = ServicePaths.For(OsKind.Linux, Env(("TGWS_SERVICE_ROOT", tmp.Combine("root"))));
        Directory.CreateDirectory(p.ExclusionsDir);
        File.WriteAllText(p.Lists.ExcludeIps, "203.0.113.0/24\n");

        p.EnsureExclusionFiles();

        Assert.Equal(ServicePaths.ExclusionsHeader + "\n", File.ReadAllText(p.Lists.ExcludeHosts));
        Assert.Empty(Exclusions.Read(p.Lists.ExcludeHosts));
        Assert.Equal("203.0.113.0/24\n", File.ReadAllText(p.Lists.ExcludeIps));
    }
}
