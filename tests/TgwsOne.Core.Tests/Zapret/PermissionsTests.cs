using System.Runtime.Versioning;
using TgwsOne.Core.Zapret;

namespace TgwsOne.Core.Tests.Zapret;

/// <summary>
/// nfqws2 drops root privileges (to uid 0x7FFFFFFF) after start-up and then re-reads the list files and writes the Lua
/// state file; <c>--dry-run</c> checks file access under those dropped privileges too. Everything the service shares with
/// it must therefore stay readable and traversable for everybody. A 0700 folder (which SP1's <see cref="IniFile.Save()"/>
/// makes of whatever it saves into) would make the next dry run fail with a permission error.
/// </summary>
[Collection(EnvCollection.Name)]
[SupportedOSPlatform("linux")]
public class PermissionsTests : IDisposable
{
    private const UnixFileMode Everyone = UnixFileMode.OtherRead | UnixFileMode.OtherExecute | UnixFileMode.GroupRead | UnixFileMode.GroupExecute;
    private readonly FakeZapretEnv _env = new();

    public void Dispose() => _env.Dispose();

    private static UnixFileMode Mode(string path) => File.GetUnixFileMode(path) & (UnixFileMode)0x1FF;

    private static void AssertReachable(string dir) => Assert.Equal(Everyone, Mode(dir) & Everyone);

    private static void MakeDir(string path, UnixFileMode mode)
    {
        Directory.CreateDirectory(path, mode);
        File.SetUnixFileMode(path, mode);   // whatever the umask did
    }

    private const UnixFileMode Mode0700 = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute;

    [LinuxFact]
    public void PublicDirectory_OpensAPrivateFolder_WithoutRemovingAnything()
    {
        using var tmp = new TempDir();
        string closed = tmp.Combine("closed");
        MakeDir(closed, Mode0700);
        string group = tmp.Combine("group");
        MakeDir(group, Mode0700 | UnixFileMode.GroupWrite | UnixFileMode.GroupRead | UnixFileMode.GroupExecute);

        PublicDirectory.Ensure(closed);
        PublicDirectory.Ensure(group);

        Assert.Equal(Mode0700 | Everyone, Mode(closed));
        Assert.Equal(Mode0700 | Everyone | UnixFileMode.GroupWrite, Mode(group));
    }

    [LinuxFact]
    public void PublicDirectory_CreatesMissingFolders_AndLeavesAnOpenOneAlone()
    {
        using var tmp = new TempDir();
        string nested = tmp.Combine("a", "b");

        PublicDirectory.Ensure(nested);
        AssertReachable(nested);

        File.SetUnixFileMode(nested, (UnixFileMode)0x1ED);   // 0755
        PublicDirectory.Ensure(nested);
        Assert.Equal((UnixFileMode)0x1ED, Mode(nested));
    }

    [LinuxFact]
    public void SavingTheState_DoesNotMakeItsFolderPrivate()
    {
        using var tmp = new TempDir();
        string dir = tmp.Combine("state");
        MakeDir(dir, (UnixFileMode)0x1ED);   // 0755, as systemd's StateDirectory makes it

        new ServiceState(false, "split").Save(Path.Combine(dir, "state.ini"));

        Assert.Equal((UnixFileMode)0x1ED, Mode(dir));
    }

    [LinuxFact]
    public void SavingTheState_OpensAFolderAnOlderRunClosed()
    {
        using var tmp = new TempDir();
        string dir = tmp.Combine("state");
        MakeDir(dir, Mode0700);

        new ServiceState(true, "auto").Save(Path.Combine(dir, "state.ini"));

        AssertReachable(dir);
    }

    [LinuxFact]
    public void TheAppsOwnSettings_StillGoToAPrivateFolder()
    {
        // SP1's behaviour is unchanged: only the service opts out.
        using var tmp = new TempDir();
        string dir = tmp.Combine("settings");
        MakeDir(dir, (UnixFileMode)0x1ED);
        var ini = IniFile.Load(Path.Combine(dir, "tgwsone.ini"));
        ini.Set("proxy", "port", "1443");

        ini.Save();

        Assert.Equal(Mode0700, Mode(dir));
    }

    [LinuxFact]
    public void Exclusions_Write_LeavesTheFolderAndTheFileReadableForEveryone()
    {
        using var tmp = new TempDir();
        string dir = tmp.Combine("exclusions");
        MakeDir(dir, Mode0700);
        string file = Path.Combine(dir, "exclude-hosts.txt");

        Exclusions.Write(file, ["a.example.com"], "# h");

        AssertReachable(dir);
        Assert.Equal((UnixFileMode)0x1A4, Mode(file));   // 0644
    }

    [LinuxFact]
    public void ServicePaths_SeedAndEnsure_ProduceFilesNfqws2CanRead()
    {
        using var tmp = new TempDir();
        string app = tmp.Combine("app-lists");
        Directory.CreateDirectory(app);
        File.WriteAllText(Path.Combine(app, "telegram.txt"), "telegram.org\n");
        File.WriteAllText(Path.Combine(app, "cloudflare-ips.txt"), "173.245.48.0/20\n");
        File.SetUnixFileMode(Path.Combine(app, "telegram.txt"), UnixFileMode.UserRead | UnixFileMode.UserWrite);   // as a strict umask leaves it
        ServicePaths p = ServicePaths.For(OsKind.Linux, n => n == "TGWS_SERVICE_ROOT" ? tmp.Combine("root") : null);
        MakeDir(tmp.Combine("root"), (UnixFileMode)0x1ED);
        MakeDir(p.ExclusionsDir, Mode0700);   // what an older run left behind

        p.SeedTargetLists(app);
        p.EnsureExclusionFiles();

        AssertReachable(p.ListsDir);
        AssertReachable(p.ExclusionsDir);
        Assert.Equal((UnixFileMode)0x1A4, Mode(p.Lists.TelegramHosts));
        Assert.Equal((UnixFileMode)0x1A4, Mode(p.Lists.CloudflareIps));
        Assert.Equal((UnixFileMode)0x1A4, Mode(p.Lists.ExcludeHosts));
        Assert.Equal((UnixFileMode)0x1A4, Mode(p.Lists.ExcludeIps));
    }

    [LinuxFact]
    public async Task Start_OpensEveryFolderNfqws2NeedsToReach_EvenIfAnOlderRunClosedThem()
    {
        await using ServiceRig rig = await ServiceRig.StartAsync(_env, beforeStart: p =>
        {
            foreach (string dir in new[] { p.ListsDir, p.ExclusionsDir, p.StateDir })
                MakeDir(dir, Mode0700);
        });

        AssertReachable(rig.Paths.ListsDir);
        AssertReachable(rig.Paths.ExclusionsDir);
        AssertReachable(rig.Paths.StateDir);
    }

    [LinuxFact]
    public async Task OnOffUseAndExclusions_NeverCloseTheFoldersAgain()
    {
        // The regression: the first state save used to make the folder private, and the next dry run (as the
        // unprivileged uid) then could not read the lists any more.
        await using ServiceRig rig = await ServiceRig.StartAsync(_env);
        string stateFolder = Path.GetDirectoryName(rig.Paths.StateIni)!;

        Assert.True((await rig.Send("use", "split")).Ok);
        Assert.True((await rig.Send("off")).Ok);
        Assert.True((await rig.Send("on")).Ok);
        Assert.True((await rig.Send(new ControlRequest(ControlCommands.SetExclusions, Hosts: ["a.example.com"], Ips: []))).Ok);

        AssertReachable(stateFolder);
        AssertReachable(rig.Paths.ListsDir);
        AssertReachable(rig.Paths.ExclusionsDir);
        AssertReachable(rig.Paths.StateDir);
        Assert.Equal((UnixFileMode)0x1A4, Mode(rig.Paths.Lists.ExcludeHosts));
    }
}
