using System.Diagnostics;
using System.Net;
using System.Net.Sockets;

namespace TgwsOne.Core.Tests;

[Collection(EnvCollection.Name)]
public class ProxyControllerTests : IDisposable
{
    // A start stamp that matches no real process (an unknown boot, or no such time).
    private static readonly string StaleStamp = OperatingSystem.IsLinux()
        ? "proc:00000000-0000-0000-0000-000000000000:1"
        : "time:1";

    private readonly TestEnv _env = new();

    public void Dispose() => _env.Dispose();

    private AppPaths Paths => _env.Paths;

    private ProxyController NewController(TimeSpan? startTimeout = null)
        => new(Paths, new AppLog(Paths.AppLogFile), startTimeout ?? TimeSpan.FromSeconds(10));

    /// <summary>Writes the listen port into the INI (a fresh free port unless given).</summary>
    private int Configure(int? port = null)
    {
        int p = port ?? TestEnv.FreePort();
        var ini = IniFile.Load(Paths.Ini);
        ini.Set("proxy", "port", p.ToString());
        ini.Save();
        return p;
    }

    private void SetIni(string section, string key, string value)
    {
        var ini = IniFile.Load(Paths.Ini);
        ini.Set(section, key, value);
        ini.Save();
    }

    private static bool Listening(int port) => PortProbe.CanConnect(IPAddress.Loopback, port, 300);

    private static bool ProcessAlive(int pid)
    {
        try
        {
            using Process p = Process.GetProcessById(pid);
            _ = p.MainModule;   // throws for a zombie or a process that is gone
            return !p.HasExited;
        }
        catch { return false; }
    }

    // ---------- start / status / stop ----------

    [Fact]
    public async Task Start_Status_Stop()
    {
        int port = Configure();
        var c = NewController();

        StartResult r = await c.StartAsync();

        Assert.Equal(StartOutcome.Started, r.Outcome);
        Assert.Equal(port, r.Port);
        Assert.True(r.Ok);
        ProxyStatus st = c.GetStatus();
        Assert.True(st.Running);
        Assert.True(st.Listening);
        Assert.Equal(port, st.Port);
        Assert.False(st.PortBusyByOther);
        Assert.NotNull(st.Pid);
        Assert.True(File.Exists(Paths.PidFile));

        Assert.Equal(StopOutcome.Stopped, await c.StopAsync());

        Assert.False(File.Exists(Paths.PidFile));
        Assert.True(TestEnv.WaitFor(() => !Listening(port)), "port still listening after stop");
        ProxyStatus after = c.GetStatus();
        Assert.False(after.Running);
        Assert.False(after.Listening);
        Assert.Equal(StopOutcome.NotRunning, await c.StopAsync());
    }

    [Fact]
    public async Task Start_Twice_AlreadyRunning()
    {
        Configure();
        var c = NewController();
        await c.StartAsync();
        int pid = c.GetStatus().Pid!.Value;

        StartResult second = await c.StartAsync();

        Assert.Equal(StartOutcome.AlreadyRunning, second.Outcome);
        Assert.True(second.Ok);
        Assert.Equal(pid, c.GetStatus().Pid);
        Assert.Single(TestEnv.FakeEngines());
    }

    [Fact]
    public async Task Start_PortBusy_SwitchesAndSaves()
    {
        int busy = Configure();
        using var blocker = new TcpListener(IPAddress.Loopback, busy);
        blocker.Start();
        var c = NewController();

        StartResult r = await c.StartAsync();

        Assert.Equal(StartOutcome.PortSwitched, r.Outcome);
        Assert.True(r.Ok);
        Assert.Equal(busy, r.OldPort);
        Assert.InRange(r.Port, busy + 1, busy + 99);
        Assert.Equal(r.Port.ToString(), IniFile.Load(Paths.Ini).Get("proxy", "port"));
        Assert.True(Listening(r.Port));
        Assert.Equal(r.Port, c.GetStatus().Port);
        Assert.Contains($"порт {busy} занят — перехожу на {r.Port}", File.ReadAllText(Paths.AppLogFile));
    }

    [Fact]
    public async Task Start_PortsExhausted()
    {
        var held = TestEnv.BindConsecutive(100);   // the configured port and the 99 after it
        try
        {
            int first = ((IPEndPoint)held[0].LocalEndpoint).Port;
            Configure(first);

            StartResult r = await NewController().StartAsync();

            Assert.Equal(StartOutcome.PortExhausted, r.Outcome);
            Assert.False(r.Ok);
            Assert.Equal(first.ToString(), IniFile.Load(Paths.Ini).Get("proxy", "port"));   // not switched to a busy port
            Assert.Empty(TestEnv.FakeEngines());
            Assert.False(File.Exists(Paths.PidFile));
        }
        finally { foreach (var l in held) l.Stop(); }
    }

    [Fact]
    public async Task Start_EngineMissing()
    {
        Configure();
        _env.Set("TGWS_ENGINE", Path.Combine(_env.DataDir, "no-such-engine"));

        StartResult r = await NewController().StartAsync();

        Assert.Equal(StartOutcome.EngineMissing, r.Outcome);
        Assert.False(r.Ok);
        Assert.False(File.Exists(Paths.PidFile));
        Assert.Contains("движок не найден", File.ReadAllText(Paths.AppLogFile));
    }

    [Fact]
    public async Task Start_EngineExits_ReportsCodeAndOutput()
    {
        Configure();
        _env.Set("FAKE_MODE", "exit:7");

        StartResult r = await NewController().StartAsync();

        Assert.Equal(StartOutcome.EngineExited, r.Outcome);
        Assert.False(r.Ok);
        Assert.Equal(7, r.ExitCode);
        Assert.Contains("fake fatal: boom", r.Output);
        Assert.False(File.Exists(Paths.PidFile));
    }

    [Fact]
    public async Task Start_EngineOutputIsLimitedToTheLastLines()
    {
        Configure();
        _env.Set("FAKE_MODE", "exit:2");
        _env.Set("FAKE_OUTPUT_LINES", "50");

        StartResult r = await NewController().StartAsync();

        // 50 numbered lines + the fatal message: only the last 20 lines come back.
        string[] lines = r.Output.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal(20, lines.Length);
        Assert.Equal("line 32", lines[0]);
        Assert.Equal("fake fatal: boom", lines[^1]);
    }

    [Fact]
    public async Task Start_NeverListens_TimeoutAndKilled()
    {
        Configure();
        _env.Set("FAKE_MODE", "nolisten");

        StartResult r = await NewController(TimeSpan.FromSeconds(1)).StartAsync();

        Assert.Equal(StartOutcome.Timeout, r.Outcome);
        Assert.False(r.Ok);
        Assert.True(TestEnv.WaitFor(() => !TestEnv.FakeEngines().Any()), "engine left running after timeout");
        Assert.False(File.Exists(Paths.PidFile));
    }

    [Fact]
    public async Task Start_Cancelled_KillsEngineAndRemovesRecord()
    {
        Configure();
        _env.Set("FAKE_MODE", "nolisten");
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(600));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => NewController().StartAsync(cts.Token));

        Assert.True(TestEnv.WaitFor(() => !TestEnv.FakeEngines().Any()), "engine left running after cancel");
        Assert.False(File.Exists(Paths.PidFile));
    }

    [Fact]
    public async Task Start_StaleRecord_IsReplaced()
    {
        Configure();
        new EngineRecord(2_000_000_000, StaleStamp, Paths.Engine, "old").Write(Paths.PidFile);

        StartResult r = await NewController().StartAsync();

        Assert.Equal(StartOutcome.Started, r.Outcome);
        Assert.NotEqual(2_000_000_000, EngineRecord.Read(Paths.PidFile)!.Pid);
    }

    [Fact]
    public async Task Start_PassesExactArgv_AndRecordsIt()
    {
        Configure();
        string argsFile = _env.Scratch("argv.txt");
        _env.Set("FAKE_ARGS_FILE", argsFile);

        StartResult r = await NewController().StartAsync();

        Assert.True(r.Ok);
        var ini = IniFile.Load(Paths.Ini);
        Assert.True(ProxySettingsStore.IsValidSecret(ini.Get("app", "secret")), "a secret should have been created and saved");
        var expected = EngineCommand.Build(ProxySettingsStore.Load(ini), Paths);
        Assert.Equal(expected, File.ReadAllLines(argsFile));

        EngineRecord rec = EngineRecord.Read(Paths.PidFile)!;
        Assert.Equal(EngineCommand.Hash(expected), rec.ArgvHash);
        Assert.Equal(Paths.Engine, rec.ExePath);
        Assert.Equal(EngineRecord.CurrentStamp(rec.Pid), rec.StartStamp);   // the engine's own start stamp
        Assert.True(rec.IsAlive());
    }

    [Fact]
    public async Task Stop_KillsChildToo()
    {
        Configure();
        string childFile = _env.Scratch("child.pid");
        _env.Set("FAKE_MODE", "child");
        _env.Set("FAKE_CHILD_PID_FILE", childFile);
        var c = NewController();
        Assert.True((await c.StartAsync()).Ok);
        int child = int.Parse(File.ReadAllText(childFile));
        Assert.True(ProcessAlive(child), "the helper child should be running before stop");

        await c.StopAsync();

        Assert.True(TestEnv.WaitFor(() => !ProcessAlive(child)), "child process survived stop");
    }

    [Fact]
    public async Task Stop_StaleRecord_NotRunning()
    {
        new EngineRecord(2_000_000_000, StaleStamp, Paths.Engine, "h").Write(Paths.PidFile);

        StopOutcome r = await NewController().StopAsync();

        Assert.Equal(StopOutcome.NotRunning, r);
        Assert.False(File.Exists(Paths.PidFile));
    }

    [Fact]
    public async Task Stop_DoesNotKillAProcessThatOnlyReusesTheRecordedPid()
    {
        // A foreign process that runs the very same executable and now holds the PID the record names,
        // as after a reboot or a PID wrap. It must read as "not ours" and survive a stop.
        _env.Set("FAKE_MODE", "nolisten");
        using Process stranger = DetachedLauncher.Start(Paths.Engine, [], _env.Scratch("c.log"), AppContext.BaseDirectory);
        Assert.True(TestEnv.WaitFor(() => EngineRecord.ForProcess(stranger, Paths.Engine, "h").IsAlive()));
        new EngineRecord(stranger.Id, StaleStamp, Paths.Engine, "h").Write(Paths.PidFile);
        var c = NewController();

        Assert.False(c.GetStatus().Running);
        Assert.Equal(StopOutcome.NotRunning, await c.StopAsync());

        Assert.False(stranger.HasExited, "StopAsync killed a process that was not ours");
        Assert.False(File.Exists(Paths.PidFile));
    }

    [Fact]
    public async Task AnotherControllerInstance_SeesAndStopsTheEngine()
    {
        // The CLI and the GUI are separate processes: all state must live in the INI and the pid file.
        int port = Configure();
        await NewController().StartAsync();

        var other = NewController();
        Assert.True(other.GetStatus().Running);
        Assert.Equal(StartOutcome.AlreadyRunning, (await other.StartAsync()).Outcome);
        Assert.Equal(StopOutcome.Stopped, await other.StopAsync());
        Assert.True(TestEnv.WaitFor(() => !Listening(port)));
    }

    // ---------- start lock ----------

    /// <summary>Holds the start lock the way another starting process would.</summary>
    private FileStream HoldStartLock()
    {
        PrivateDirectory.Ensure(Paths.RunDir);
        return new FileStream(Paths.StartLockFile, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None, 1);
    }

    [Fact]
    public async Task Start_WaitsForTheLock_ThenProceeds()
    {
        Configure();
        var held = HoldStartLock();
        Task<StartResult> start = NewController().StartAsync();

        await Task.Delay(700);
        Assert.False(start.IsCompleted, "StartAsync should be waiting for the lock");
        Assert.Empty(TestEnv.FakeEngines());

        held.Dispose();
        StartResult r = await start;

        Assert.Equal(StartOutcome.Started, r.Outcome);
    }

    [Fact]
    public async Task Start_LockHeldTooLong_TimesOutWithoutStartingAnything()
    {
        Configure();
        using var held = HoldStartLock();
        var c = new ProxyController(Paths, new AppLog(Paths.AppLogFile), TimeSpan.FromSeconds(10), lockTimeout: TimeSpan.FromMilliseconds(500));

        await Assert.ThrowsAsync<TimeoutException>(() => c.StartAsync());

        Assert.Empty(TestEnv.FakeEngines());
        Assert.False(File.Exists(Paths.PidFile));
    }

    [Fact]
    public async Task Start_CancelledWhileWaitingForTheLock()
    {
        Configure();
        using var held = HoldStartLock();
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(300));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => NewController().StartAsync(cts.Token));

        Assert.Empty(TestEnv.FakeEngines());
    }

    [Fact]
    public async Task Start_ReleasesTheLock_AndTheEngineDoesNotInheritIt()
    {
        Configure();
        Assert.True((await NewController().StartAsync()).Ok);

        // If the engine had inherited the descriptor it would still hold the lock after StartAsync returned.
        using FileStream probe = HoldStartLock();

        Assert.NotNull(probe);
    }

    [Fact]
    public async Task Start_Concurrent_OnlyOneEngine_SecondSeesAlreadyRunning()
    {
        Configure();
        var a = NewController();
        var b = NewController();

        StartResult[] results = await Task.WhenAll(a.StartAsync(), b.StartAsync());

        Assert.Equal([StartOutcome.Started, StartOutcome.AlreadyRunning], results.Select(r => r.Outcome).Order());
        Assert.All(results, r => Assert.True(r.Ok));
        Assert.Single(TestEnv.FakeEngines());
    }

    // ---------- restart needed ----------

    [Fact]
    public void RestartNeeded_NotRunning_False()
    {
        Configure();

        Assert.False(NewController().RestartNeeded());
    }

    [Fact]
    public async Task RestartNeeded_AfterNewSecret_True()
    {
        Configure();
        var c = NewController();
        await c.StartAsync();
        Assert.False(c.RestartNeeded());

        SetIni("app", "secret", ProxySettingsStore.NewSecret());

        Assert.True(c.RestartNeeded());
    }

    [Fact]
    public async Task RestartNeeded_AfterPortChange_True()
    {
        int port = Configure();
        var c = NewController();
        await c.StartAsync();
        Assert.False(c.RestartNeeded());

        SetIni("proxy", "port", (port + 1).ToString());

        Assert.True(c.RestartNeeded());
    }

    [Fact]
    public async Task RestartNeeded_AfterTraytip_False()
    {
        Configure();
        var c = NewController();
        await c.StartAsync();

        SetIni("app", "traytip", "0");          // unrelated to the engine
        SetIni("zapret", "strategy", "other");

        Assert.False(c.RestartNeeded());
    }

    [Fact]
    public async Task RestartNeeded_InvalidSecretInIni_True()
    {
        Configure();
        var c = NewController();
        await c.StartAsync();

        SetIni("app", "secret", "zz");

        Assert.True(c.RestartNeeded());
    }
}
