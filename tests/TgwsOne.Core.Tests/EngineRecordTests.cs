using System.Diagnostics;
using System.Globalization;

namespace TgwsOne.Core.Tests;

[Collection(EnvCollection.Name)]
public class EngineRecordTests : IDisposable
{
    private const string BootA = "3f2a9c1e-7b44-4d0a-9a1e-2c5d6e7f8a90";
    private readonly TestEnv _env = new();

    public void Dispose() => _env.Dispose();

    private string PidFile => _env.Paths.PidFile;

    private Process LaunchIdleEngine()
    {
        _env.Set("FAKE_MODE", "nolisten");
        return DetachedLauncher.Start(TestEnv.FakeEnginePath, [], _env.Scratch("c.log"), AppContext.BaseDirectory);
    }

    // ---------- file format ----------

    [Theory]
    [InlineData($"proc:{BootA}:123456")]
    [InlineData("time:639000000000000000")]
    public void Record_WriteRead_RoundTrip(string stamp)
    {
        var rec = new EngineRecord(4242, stamp, "/opt/app/tgws-engine", "abc123");

        rec.Write(PidFile);   // also creates the run directory
        var back = EngineRecord.Read(PidFile);

        Assert.Equal(rec, back);
    }

    [Theory]
    [InlineData("")]
    [InlineData("garbage")]
    [InlineData("12\nproc:b:5\n/path\n")]                    // too few lines
    [InlineData("x\nproc:b:5\n/path\nhash")]                 // pid not a number
    [InlineData("12\nnot-a-stamp\n/path\nhash")]             // unknown stamp kind
    [InlineData("12\ntime:abc\n/path\nhash")]                // time stamp not a number
    [InlineData("12\nproc:b:\n/path\nhash")]                 // empty start ticks
    [InlineData("12\nproc::5\n/path\nhash")]                 // empty boot id
    [InlineData("12\n638000000000000000\n/path\nhash")]      // the old format: a bare tick count is not a stamp
    [InlineData("12\nproc:b:5\n\nhash")]                     // empty path
    [InlineData("12\nproc:b:5\n/path\n\n")]                  // empty hash (a fourth, empty line)
    public void Record_Malformed_ReadsNull(string content)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(PidFile)!);
        File.WriteAllText(PidFile, content);

        Assert.Null(EngineRecord.Read(PidFile));
    }

    [Fact]
    public void Record_Missing_ReadsNull() => Assert.Null(EngineRecord.Read(PidFile));

    [Fact]
    public void Record_Delete_RemovesFileAndToleratesMissing()
    {
        new EngineRecord(1, "time:1", "/x", "h").Write(PidFile);

        EngineRecord.Delete(PidFile);
        EngineRecord.Delete(PidFile);

        Assert.False(File.Exists(PidFile));
    }

    // ---------- the start stamp ----------

    [Fact]
    public void CurrentStamp_OnLinux_IsBootIdAndStartTicks_AndStable()
    {
        if (!OperatingSystem.IsLinux()) return;

        string? stamp = EngineRecord.CurrentStamp(Environment.ProcessId);

        Assert.NotNull(stamp);
        Assert.Matches("^proc:[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}:[0-9]+$", stamp);
        Assert.Equal(File.ReadAllText("/proc/sys/kernel/random/boot_id").Trim(), stamp.Split(':')[1]);
        Assert.Equal(stamp, EngineRecord.CurrentStamp(Environment.ProcessId));
    }

    [Fact]
    public void CurrentStamp_DiffersBetweenProcesses_AndIsNullForAMissingPid()
    {
        using Process p = LaunchIdleEngine();
        string? engine = null;
        Assert.True(TestEnv.WaitFor(() => (engine = EngineRecord.CurrentStamp(p.Id)) != null));

        Assert.NotEqual(EngineRecord.CurrentStamp(Environment.ProcessId), engine);
        Assert.Null(EngineRecord.CurrentStamp(2_000_000_000));
    }

    [Fact]
    public void SameStamp_Linux_ComparesExactly()
    {
        Assert.True(EngineRecord.SameStamp($"proc:{BootA}:1000", $"proc:{BootA}:1000"));
        Assert.False(EngineRecord.SameStamp($"proc:{BootA}:1000", $"proc:{BootA}:1001"));   // one tick apart is another process
        Assert.False(EngineRecord.SameStamp($"proc:{BootA}:1000", "proc:00000000-0000-0000-0000-000000000000:1000"));   // another boot
    }

    [Fact]
    public void SameStamp_Time_ToleratesLessThanOneSecond()
    {
        long t = 639_000_000_000_000_000;
        long second = TimeSpan.TicksPerSecond;

        Assert.True(EngineRecord.SameStamp($"time:{t}", $"time:{t}"));
        Assert.True(EngineRecord.SameStamp($"time:{t}", $"time:{t + second / 2}"));
        Assert.True(EngineRecord.SameStamp($"time:{t}", $"time:{t - second / 2}"));
        Assert.False(EngineRecord.SameStamp($"time:{t}", $"time:{t + second}"));        // 1 s is already another process
        Assert.False(EngineRecord.SameStamp($"time:{t}", $"time:{t - 2 * second}"));
    }

    [Fact]
    public void SameStamp_DifferentKinds_NeverMatch()
    {
        Assert.False(EngineRecord.SameStamp($"proc:{BootA}:1000", "time:1000"));
        Assert.False(EngineRecord.SameStamp("time:1000", $"proc:{BootA}:1000"));
        Assert.False(EngineRecord.SameStamp("garbage", "garbage2"));
    }

    // ---------- is it ours ----------

    [Fact]
    public void Record_ForRunningProcess_IsAlive()
    {
        using Process p = LaunchIdleEngine();
        var rec = EngineRecord.ForProcess(p, TestEnv.FakeEnginePath, "h");

        Assert.True(TestEnv.WaitFor(rec.IsAlive), "a record made from a live process should be alive once it runs the engine");
    }

    [Fact]
    public void Record_DeadPid_NotAlive()
    {
        using Process p = LaunchIdleEngine();
        var rec = EngineRecord.ForProcess(p, TestEnv.FakeEnginePath, "h");
        Assert.True(TestEnv.WaitFor(rec.IsAlive));

        p.Kill(entireProcessTree: true);
        p.WaitForExit(10_000);

        // Same PID, genuine path: only the process being gone makes it "not ours".
        Assert.False(rec.IsAlive());
    }

    [Fact]
    public void Record_ReusedPid_DifferentStartStamp_NotAlive()
    {
        using Process p = LaunchIdleEngine();
        var genuine = EngineRecord.ForProcess(p, TestEnv.FakeEnginePath, "h");
        Assert.True(TestEnv.WaitFor(genuine.IsAlive));
        // Same PID, same executable, but the record is of an earlier process that had this PID.
        if (OperatingSystem.IsLinux())
        {
            string[] parts = genuine.StartStamp.Split(':');   // proc : boot id : ticks
            long ticks = long.Parse(parts[2], CultureInfo.InvariantCulture);

            Assert.False((genuine with { StartStamp = $"proc:{parts[1]}:{ticks + 1}" }).IsAlive());     // started one tick apart
            Assert.False((genuine with { StartStamp = $"proc:{parts[1]}:{ticks - 1}" }).IsAlive());
            Assert.False((genuine with { StartStamp = $"proc:{BootA}:{ticks}" }).IsAlive());            // same ticks, earlier boot
            Assert.False((genuine with { StartStamp = "time:" + DateTime.UtcNow.Ticks }).IsAlive());    // a stamp of another kind
        }
        Assert.True(genuine.IsAlive());   // and the genuine one still is
    }

    [Fact]
    public void Record_DifferentPath_NotAlive()
    {
        using Process self = Process.GetCurrentProcess();

        Assert.True(EngineRecord.ForProcess(self, Environment.ProcessPath!, "h").IsAlive());   // control: it can say yes
        Assert.False(EngineRecord.ForProcess(self, "/nonexistent/tgws-engine", "h").IsAlive());
    }

    [Fact]
    public void Record_PathThroughSymlink_StillMatches()
    {
        if (OperatingSystem.IsWindows()) return;
        using Process self = Process.GetCurrentProcess();
        string link = _env.Scratch("dotnet-link");
        File.CreateSymbolicLink(link, Environment.ProcessPath!);

        Assert.True(EngineRecord.ForProcess(self, link, "h").IsAlive());
    }

    [Fact]
    public void Record_NegativeOrZeroPid_NotAlive()
    {
        Assert.False(new EngineRecord(0, "time:1", "/x", "h").IsAlive());
        Assert.False(new EngineRecord(-5, "time:1", "/x", "h").IsAlive());
    }

    [Fact]
    public void ForProcess_OfAnExitedProcess_Throws()
    {
        _env.Set("FAKE_MODE", "exit:0");
        using Process p = DetachedLauncher.Start(TestEnv.FakeEnginePath, [], _env.Scratch("c.log"), AppContext.BaseDirectory);
        p.WaitForExit(10_000);

        Assert.Throws<InvalidOperationException>(() => EngineRecord.ForProcess(p, TestEnv.FakeEnginePath, "h"));
    }
}
