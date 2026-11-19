using System.Diagnostics;

namespace TgwsOne.Core.Tests;

[Collection(EnvCollection.Name)]
public class EngineRecordTests : IDisposable
{
    private readonly TestEnv _env = new();

    public void Dispose() => _env.Dispose();

    private string PidFile => _env.Paths.PidFile;

    [Fact]
    public void Record_WriteRead_RoundTrip()
    {
        var started = new DateTime(2026, 11, 19, 12, 30, 45, DateTimeKind.Utc).AddTicks(1234567);
        var rec = new EngineRecord(4242, started, "/opt/app/tgws-engine", "abc123");

        rec.Write(PidFile);   // also creates the run directory
        var back = EngineRecord.Read(PidFile);

        Assert.Equal(rec, back);
        Assert.Equal(DateTimeKind.Utc, back!.StartTimeUtc.Kind);
    }

    [Theory]
    [InlineData("")]
    [InlineData("garbage")]
    [InlineData("12\n34\n/path\n")]                    // too few lines, start ticks not a number
    [InlineData("x\n638000000000000000\n/path\nhash")] // pid not a number
    [InlineData("12\nx\n/path\nhash")]                 // ticks not a number
    [InlineData("12\n638000000000000000\n\nhash")]     // empty path
    [InlineData("12\n638000000000000000\n/path\n")]    // empty hash
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
        new EngineRecord(1, DateTime.UtcNow, "/x", "h").Write(PidFile);

        EngineRecord.Delete(PidFile);
        EngineRecord.Delete(PidFile);

        Assert.False(File.Exists(PidFile));
    }

    [Fact]
    public void Record_DeadPid_NotAlive()
    {
        _env.Set("FAKE_MODE", "exit:0");
        using Process p = DetachedLauncher.Start(TestEnv.FakeEnginePath, [], _env.Scratch("c.log"), AppContext.BaseDirectory);
        p.WaitForExit(10_000);

        // Same PID, genuine path: only the process being gone makes it "not ours".
        Assert.True(p.HasExited);
        Assert.False(new EngineRecord(p.Id, DateTime.UtcNow, TestEnv.FakeEnginePath, "h").IsAlive());
    }

    [Fact]
    public void Record_ReusedPid_DifferentStartTime_NotAlive()
    {
        _env.Set("FAKE_MODE", "nolisten");
        using Process p = DetachedLauncher.Start(TestEnv.FakeEnginePath, [], _env.Scratch("c.log"), AppContext.BaseDirectory);
        var genuine = new EngineRecord(p.Id, p.StartTime.ToUniversalTime(), TestEnv.FakeEnginePath, "h");
        Assert.True(TestEnv.WaitFor(genuine.IsAlive), "engine did not come up");

        // A different process that happens to reuse the PID started at another time.
        Assert.False((genuine with { StartTimeUtc = genuine.StartTimeUtc.AddHours(-1) }).IsAlive());
        Assert.False((genuine with { StartTimeUtc = genuine.StartTimeUtc.AddSeconds(5) }).IsAlive());
        // Small clock jitter is tolerated.
        Assert.True((genuine with { StartTimeUtc = genuine.StartTimeUtc.AddMilliseconds(500) }).IsAlive());
    }

    [Fact]
    public void Record_DifferentPath_NotAlive()
    {
        using Process self = Process.GetCurrentProcess();
        DateTime start = self.StartTime.ToUniversalTime();

        Assert.True(new EngineRecord(self.Id, start, Environment.ProcessPath!, "h").IsAlive());       // control: it can say yes
        Assert.False(new EngineRecord(self.Id, start, "/nonexistent/tgws-engine", "h").IsAlive());
    }

    [Fact]
    public void Record_PathThroughSymlink_StillMatches()
    {
        if (OperatingSystem.IsWindows()) return;
        using Process self = Process.GetCurrentProcess();
        string link = _env.Scratch("dotnet-link");
        File.CreateSymbolicLink(link, Environment.ProcessPath!);

        Assert.True(new EngineRecord(self.Id, self.StartTime.ToUniversalTime(), link, "h").IsAlive());
    }

    [Fact]
    public void Record_NegativeOrZeroPid_NotAlive()
    {
        Assert.False(new EngineRecord(0, DateTime.UtcNow, "/x", "h").IsAlive());
        Assert.False(new EngineRecord(-5, DateTime.UtcNow, "/x", "h").IsAlive());
    }
}
