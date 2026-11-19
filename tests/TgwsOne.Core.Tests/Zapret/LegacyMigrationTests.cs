using TgwsOne.Core.Zapret;
using TgwsOne.Core.Zapret.Windows;

namespace TgwsOne.Core.Tests.Zapret;

public class LegacyMigrationTests
{
    private const string AppDir = @"C:\Program Files\TgwsOne\";

    [Fact]
    public void Plan_StopsThenDeletesTheScheduledTask()
    {
        var plan = LegacyMigration.Plan();

        Assert.Equal(2, plan.Count);
        Assert.Equal("schtasks.exe", plan[0].File);
        Assert.Equal(["/end", "/tn", "TgwsOne-Zapret"], plan[0].Args);
        Assert.Equal("schtasks.exe", plan[1].File);
        Assert.Equal(["/delete", "/f", "/tn", "TgwsOne-Zapret"], plan[1].Args);
    }

    [Fact]
    public async Task Run_IssuesExactlyThePlan()
    {
        using var tmp = new TempDir();
        var runner = new FakeRunner();

        await LegacyMigration.RunAsync(runner, AppDir, tmp.Path, processes: () => [], kill: _ => { });

        Assert.Equal(
            [("schtasks.exe", "/end /tn TgwsOne-Zapret"), ("schtasks.exe", "/delete /f /tn TgwsOne-Zapret")],
            runner.Calls.Select(c => (c.File, string.Join(' ', c.Args))));
    }

    [Fact]
    public async Task Run_KillsOnlyTheWinwsUnderTheOldBinFolder()
    {
        using var tmp = new TempDir();
        var killed = new List<int>();
        (int, string)[] running =
        [
            (1, @"C:\Program Files\TgwsOne\zapret\bin\winws.exe"),         // 1.x's own
            (2, @"c:\program files\tgwsone\ZAPRET\BIN\winws.exe"),         // same place, other case
            (3, @"C:\Zapret\winws.exe"),                                   // the user's own zapret elsewhere
            (4, @"C:\Program Files\TgwsOne\zapret\binx\winws.exe"),        // a sibling that merely shares the prefix
            (5, @"C:\Program Files\TgwsOne\zapret2\winws2.exe"),           // the new bundle
            (6, @"C:\Program Files\TgwsOne2\zapret\bin\winws.exe"),        // another install
            (7, @"C:\Program Files\TgwsOne\zapret\bin\sub\winws.exe"),     // below the old folder
        ];

        await LegacyMigration.RunAsync(new FakeRunner(), AppDir, tmp.Path, processes: () => running, kill: killed.Add);

        Assert.Equal([1, 2, 7], killed);
    }

    [Fact]
    public async Task Run_AppDirWithoutATrailingSeparator_Works()
    {
        using var tmp = new TempDir();
        var killed = new List<int>();

        await LegacyMigration.RunAsync(new FakeRunner(), @"C:\Program Files\TgwsOne", tmp.Path,
            processes: () => [(1, @"C:\Program Files\TgwsOne\zapret\bin\winws.exe")], kill: killed.Add);

        Assert.Equal([1], killed);
    }

    [Fact]
    public async Task Run_DeletesTheTwoLeftoverFiles_AndNothingElse()
    {
        using var tmp = new TempDir();
        foreach (string name in new[] { "all-users.flag", "task.xml", "service.ini", "TgwsOne.ini" })
            File.WriteAllText(tmp.Combine(name), "x");
        Directory.CreateDirectory(tmp.Combine("lists"));
        File.WriteAllText(tmp.Combine("lists", "exclude-hosts.txt"), "keep.example.com\n");

        await LegacyMigration.RunAsync(new FakeRunner(), AppDir, tmp.Path, processes: () => [], kill: _ => { });

        Assert.False(File.Exists(tmp.Combine("all-users.flag")));
        Assert.False(File.Exists(tmp.Combine("task.xml")));
        Assert.True(File.Exists(tmp.Combine("service.ini")));
        Assert.True(File.Exists(tmp.Combine("TgwsOne.ini")));
        Assert.True(File.Exists(tmp.Combine("lists", "exclude-hosts.txt")));   // 1.x's exclusions survive the upgrade
    }

    [Fact]
    public async Task Run_OnACleanMachine_IsANoOp_AndIdempotent()
    {
        using var tmp = new TempDir();
        var runner = new FakeRunner { Respond = _ => (1, "ERROR: The system cannot find the file specified.") };

        await LegacyMigration.RunAsync(runner, AppDir, tmp.Path, processes: () => [], kill: _ => { });
        await LegacyMigration.RunAsync(runner, AppDir, tmp.Path, processes: () => [], kill: _ => { });

        Assert.Equal(4, runner.Calls.Count);
    }

    [Fact]
    public async Task Run_AFailedKill_DoesNotStopTheRest()
    {
        using var tmp = new TempDir();
        File.WriteAllText(tmp.Combine("all-users.flag"), "1");
        var killed = new List<int>();
        string logFile = tmp.Combine("log.txt");

        await LegacyMigration.RunAsync(new FakeRunner(), AppDir, tmp.Path,
            processes: () => [(1, @"C:\Program Files\TgwsOne\zapret\bin\winws.exe"), (2, @"C:\Program Files\TgwsOne\zapret\bin\winws.exe")],
            kill: pid => { if (pid == 1) throw new InvalidOperationException("access denied"); killed.Add(pid); },
            log: new AppLog(logFile));

        Assert.Equal([2], killed);
        Assert.False(File.Exists(tmp.Combine("all-users.flag")));
        string log = File.ReadAllText(logFile);
        Assert.Contains("не удалось остановить pid 1: access denied", log);
        Assert.Contains("остановлен winws.exe (pid 2)", log);
    }

    [Fact]
    public async Task Run_LogsTheTaskRemovalOnlyWhenItSucceeded()
    {
        using var tmp = new TempDir();
        string logFile = tmp.Combine("log.txt");

        await LegacyMigration.RunAsync(new FakeRunner { Respond = _ => (1, "not found") }, AppDir, tmp.Path, processes: () => [], kill: _ => { }, log: new AppLog(logFile));
        Assert.False(File.Exists(logFile));

        await LegacyMigration.RunAsync(new FakeRunner(), AppDir, tmp.Path, processes: () => [], kill: _ => { }, log: new AppLog(logFile));
        Assert.Contains("задача TgwsOne-Zapret удалена", File.ReadAllText(logFile));
    }
}
