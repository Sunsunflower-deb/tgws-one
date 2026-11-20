using TgwsOne.Core.Zapret;

namespace TgwsOne.Core.Tests.Zapret;

[Collection(EnvCollection.Name)]
public class ZapretServiceTests : IDisposable
{
    private readonly FakeZapretEnv _env = new();

    public void Dispose() => _env.Dispose();

    private static bool WaitFor(Func<bool> condition, int timeoutMs = 8000) => TestEnv.WaitFor(condition, timeoutMs);

    [LinuxFact]
    public async Task Start_WithDefaults_RunsAuto_AndPreparesTheFiles()
    {
        await using ServiceRig rig = await ServiceRig.StartAsync(_env);

        Assert.True(WaitFor(() => _env.Args.Length > 0));
        Assert.Equal(rig.ExpectedArgv("auto"), _env.Args);
        // Target lists were seeded from the app, the exclusion files created, the state folder made for --writable.
        Assert.Contains("telegram.org", File.ReadAllText(rig.Paths.Lists.TelegramHosts));
        Assert.Contains("173.245.48.0/20", File.ReadAllText(rig.Paths.Lists.CloudflareIps));
        Assert.True(File.Exists(rig.Paths.Lists.ExcludeHosts));
        Assert.True(File.Exists(rig.Paths.Lists.ExcludeIps));
        Assert.True(Directory.Exists(rig.Paths.StateDir));

        ControlResponse status = await rig.Send("status");
        Assert.True(status.Ok);
        Assert.Equal("auto", status.Status!.Strategy);
        Assert.True(status.Status.Enabled);
        Assert.Equal("running", status.Status.State);
        Assert.NotNull(status.Status.Pid);
        Assert.NotNull(status.Status.Since);
        Assert.Equal(0, status.Status.Restarts);
        Assert.Null(status.Status.LastError);
        Assert.Equal("v1.0.7", status.Status.Bundle);
    }

    [LinuxFact]
    public async Task Start_DoesNotOverwriteAdminLists()
    {
        await using ServiceRig rig = await ServiceRig.StartAsync(_env, beforeStart: p =>
        {
            Directory.CreateDirectory(p.ListsDir);
            File.WriteAllText(p.Lists.TelegramHosts, "edited.example.com\n");
        });

        Assert.Equal("edited.example.com\n", File.ReadAllText(rig.Paths.Lists.TelegramHosts));
    }

    [LinuxFact]
    public async Task Start_LoadsTheSavedState_Disabled()
    {
        await using ServiceRig rig = await ServiceRig.StartAsync(_env, beforeStart: p => new ServiceState(false, "split").Save(p.StateIni));

        ControlResponse status = await rig.Send("status");
        Assert.False(status.Status!.Enabled);
        Assert.Equal("split", status.Status.Strategy);
        Assert.Equal("disabled", status.Status.State);
        Assert.Null(status.Status.Pid);
        Assert.Empty(_env.Args);

        ControlResponse on = await rig.Send("on");
        Assert.True(on.Ok);
        Assert.True(WaitFor(() => _env.Args.SequenceEqual(rig.ExpectedArgv("split"))), string.Join('|', _env.Args));
    }

    [LinuxFact]
    public async Task Start_SavedStrategyIsGone_FallsBackToAuto_AndSavesIt()
    {
        await using ServiceRig rig = await ServiceRig.StartAsync(_env, beforeStart: p => new ServiceState(true, "custom:vanished").Save(p.StateIni));

        Assert.True(WaitFor(() => _env.Args.SequenceEqual(rig.ExpectedArgv("auto"))), string.Join('|', _env.Args));
        Assert.Equal("auto", ServiceState.Load(rig.Paths.StateIni).Strategy);
        Assert.Contains("custom:vanished", _env.ServiceLogText);
    }

    [LinuxFact]
    public async Task Start_BundleMissing_DoesNotThrow_AndReportsAnError()
    {
        await using ServiceRig rig = await ServiceRig.StartAsync(_env, bundleMissing: true);

        ControlResponse status = await rig.Send("status");

        Assert.True(status.Ok);
        Assert.Equal("error", status.Status!.State);
        Assert.Contains("пакет zapret2 не найден", status.Status.LastError);
        Assert.Contains("no-such-nfqws2", status.Status.LastError);

        ControlResponse on = await rig.Send("on");
        Assert.False(on.Ok);
        Assert.Contains("пакет zapret2 не найден", on.Error);
    }

    [LinuxFact]
    public async Task Use_Valid_RestartsWithTheNewArgv_AndSavesTheState()
    {
        await using ServiceRig rig = await ServiceRig.StartAsync(_env);
        Assert.True(WaitFor(() => _env.Args.Length > 0));
        int pid = rig.Runner.Pid!.Value;

        ControlResponse r = await rig.Send("use", "split");

        Assert.True(r.Ok);
        Assert.Equal("split", r.Status!.Strategy);
        Assert.NotEqual(pid, r.Status.Pid);
        Assert.True(WaitFor(() => _env.Args.SequenceEqual(rig.ExpectedArgv("split"))), string.Join('|', _env.Args));
        Assert.Equal("split", IniFile.Load(rig.Paths.StateIni).Get("zapret", "strategy"));
        Assert.Equal("1", IniFile.Load(rig.Paths.StateIni).Get("zapret", "enabled"));
    }

    [LinuxFact]
    public async Task Use_Unknown_IsAnError_StateAndChildUnchanged()
    {
        await using ServiceRig rig = await ServiceRig.StartAsync(_env);
        int pid = rig.Runner.Pid!.Value;

        ControlResponse r = await rig.Send("use", "nonsense");

        Assert.False(r.Ok);
        Assert.Equal("неизвестная стратегия", r.Error);
        Assert.Equal(pid, rig.Runner.Pid);
        Assert.False(File.Exists(rig.Paths.StateIni));
        Assert.Equal("auto", (await rig.Send("status")).Status!.Strategy);
    }

    [LinuxFact]
    public async Task Use_CustomThatDoesNotExist_IsUnknown()
    {
        await using ServiceRig rig = await ServiceRig.StartAsync(_env);

        ControlResponse r = await rig.Send("use", "custom:ghost");

        Assert.Equal("неизвестная стратегия", r.Error);
    }

    [LinuxFact]
    public async Task Use_DryRunFails_ErrorReturned_OldChildStillRunning_StateUnchanged()
    {
        await using ServiceRig rig = await ServiceRig.StartAsync(_env);
        Assert.True(WaitFor(() => _env.Args.Length > 0));
        string[] before = _env.Args;
        int pid = rig.Runner.Pid!.Value;

        _env.WriteCtl("dryrun_fail=1");
        ControlResponse r = await rig.Send("use", "split");

        Assert.False(r.Ok);
        Assert.StartsWith("стратегия не прошла проверку: ", r.Error);
        Assert.Contains("bad option", r.Error);
        Assert.Equal(pid, rig.Runner.Pid);
        Assert.True(FakeZapretEnv.IsAlive(pid));
        Assert.Equal(before, _env.Args);
        Assert.False(File.Exists(rig.Paths.StateIni));
        Assert.Equal("auto", (await rig.Send("status")).Status!.Strategy);
    }

    [LinuxFact]
    public async Task Use_WhileOff_OnlyRemembersTheChoice()
    {
        await using ServiceRig rig = await ServiceRig.StartAsync(_env, beforeStart: p => new ServiceState(false, "auto").Save(p.StateIni));

        ControlResponse r = await rig.Send("use", "aggressive");

        Assert.True(r.Ok);
        Assert.Equal("aggressive", r.Status!.Strategy);
        Assert.Equal("disabled", r.Status.State);
        Assert.Empty(_env.Args);
        Assert.Equal("aggressive", ServiceState.Load(rig.Paths.StateIni).Strategy);
        Assert.False(ServiceState.Load(rig.Paths.StateIni).Enabled);
    }

    [LinuxFact]
    public async Task Off_StopsTheChild_On_StartsItAgain()
    {
        await using ServiceRig rig = await ServiceRig.StartAsync(_env);
        int pid = rig.Runner.Pid!.Value;

        ControlResponse off = await rig.Send("off");

        Assert.True(off.Ok);
        Assert.False(off.Status!.Enabled);
        Assert.Equal("disabled", off.Status.State);
        Assert.Null(off.Status.Pid);
        Assert.True(WaitFor(() => !FakeZapretEnv.IsAlive(pid)));
        Assert.Equal("0", IniFile.Load(rig.Paths.StateIni).Get("zapret", "enabled"));

        ControlResponse on = await rig.Send("on");

        Assert.True(on.Ok);
        Assert.True(on.Status!.Enabled);
        Assert.Equal("running", on.Status.State);
        Assert.NotNull(on.Status.Pid);
        Assert.Equal("1", IniFile.Load(rig.Paths.StateIni).Get("zapret", "enabled"));
    }

    [LinuxFact]
    public async Task On_WhileRunning_DoesNotRestartTheChild()
    {
        await using ServiceRig rig = await ServiceRig.StartAsync(_env);
        int pid = rig.Runner.Pid!.Value;

        ControlResponse on = await rig.Send("on");

        Assert.True(on.Ok);
        Assert.Equal(pid, rig.Runner.Pid);
    }

    [LinuxFact]
    public async Task On_AfterAFailedStart_TriesAgain()
    {
        await using ServiceRig rig = await ServiceRig.StartAsync(_env, beforeStart: _ => _env.WriteCtl("dryrun_fail=1"));
        Assert.Equal("error", (await rig.Send("status")).Status!.State);

        _env.WriteCtl();
        ControlResponse on = await rig.Send("on");

        Assert.True(on.Ok);
        Assert.Equal("running", on.Status!.State);
    }

    [LinuxFact]
    public async Task On_DryRunFails_IsAnErrorWithTheStatus()
    {
        await using ServiceRig rig = await ServiceRig.StartAsync(_env, beforeStart: p => new ServiceState(false, "auto").Save(p.StateIni));
        _env.WriteCtl("dryrun_fail=1");

        ControlResponse on = await rig.Send("on");

        Assert.False(on.Ok);
        Assert.Contains("bad option", on.Error);
        Assert.Equal("error", on.Status!.State);
        Assert.Contains("bad option", on.Status.LastError);
    }

    [LinuxFact]
    public async Task SetExclusions_Valid_WritesTheFiles_AndSignalsTheChild()
    {
        await using ServiceRig rig = await ServiceRig.StartAsync(_env);
        Assert.True(WaitFor(() => _env.Log.Contains("fake zapret running")));

        ControlResponse r = await rig.Send(new ControlRequest(ControlCommands.SetExclusions,
            Hosts: ["Sberbank.RU", "пример.рф", "sberbank.ru"], Ips: ["203.0.113.5", "10.1.2.3/8"]));

        Assert.True(r.Ok);
        Assert.Equal(["sberbank.ru", "xn--e1afmkfd.xn--p1ai"], r.Hosts);
        Assert.Equal(["203.0.113.5", "10.0.0.0/8"], r.Ips);
        Assert.Equal(["sberbank.ru", "xn--e1afmkfd.xn--p1ai"], Exclusions.Read(rig.Paths.Lists.ExcludeHosts));
        Assert.Equal(["203.0.113.5", "10.0.0.0/8"], Exclusions.Read(rig.Paths.Lists.ExcludeIps));
        Assert.StartsWith(ServicePaths.ExclusionsHeader + "\n", File.ReadAllText(rig.Paths.Lists.ExcludeHosts));
        Assert.True(WaitFor(() => _env.Hups >= 1), "the child never got SIGHUP");
    }

    [LinuxFact]
    public async Task SetExclusions_Invalid_IsAnErrorWithTheEntries_AndWritesNothing()
    {
        await using ServiceRig rig = await ServiceRig.StartAsync(_env);
        Assert.True(WaitFor(() => _env.Log.Contains("fake zapret running")));
        Assert.Null((await rig.Send(new ControlRequest(ControlCommands.SetExclusions, Hosts: ["keep.example.com"], Ips: []))).Error);
        Assert.True(WaitFor(() => _env.Hups >= 1), "the valid update never signalled the child");   // signals arrive asynchronously
        string hostsBefore = File.ReadAllText(rig.Paths.Lists.ExcludeHosts);
        string ipsBefore = File.ReadAllText(rig.Paths.Lists.ExcludeIps);
        int hups = _env.Hups;

        ControlResponse r = await rig.Send(new ControlRequest(ControlCommands.SetExclusions,
            Hosts: ["good.example.com", "bad host", "localhost"], Ips: ["300.1.1.1", "10.0.0.1"]));

        Assert.False(r.Ok);
        Assert.Equal("есть неверные записи", r.Error);
        Assert.Equal(["bad host", "localhost", "300.1.1.1"], r.Invalid);
        Assert.Equal(hostsBefore, File.ReadAllText(rig.Paths.Lists.ExcludeHosts));
        Assert.Equal(ipsBefore, File.ReadAllText(rig.Paths.Lists.ExcludeIps));
        await Task.Delay(300);
        Assert.Equal(hups, _env.Hups);
    }

    [LinuxFact]
    public async Task SetExclusions_WhileOff_WritesTheFiles_WithoutSignalling()
    {
        await using ServiceRig rig = await ServiceRig.StartAsync(_env, beforeStart: p => new ServiceState(false, "auto").Save(p.StateIni));

        ControlResponse r = await rig.Send(new ControlRequest(ControlCommands.SetExclusions, Hosts: ["a.example.com"], Ips: []));

        Assert.True(r.Ok);
        Assert.Equal(["a.example.com"], Exclusions.Read(rig.Paths.Lists.ExcludeHosts));
        Assert.Equal(0, _env.Hups);
    }

    [LinuxFact]
    public async Task SetExclusions_TheLargestValidList_IsAcceptedWhole()
    {
        await using ServiceRig rig = await ServiceRig.StartAsync(_env, beforeStart: p => new ServiceState(false, "auto").Save(p.StateIni));

        ControlResponse r = await rig.Send(MaximalExclusions.Request());

        Assert.True(r.Ok, r.Error);
        Assert.Equal(Exclusions.MaxEntries, r.Hosts!.Count);
        Assert.Equal(Exclusions.MaxEntries, r.Ips!.Count);
        Assert.Equal(Exclusions.MaxEntries, Exclusions.Read(rig.Paths.Lists.ExcludeHosts).Count);
        Assert.Equal(Exclusions.MaxEntries, Exclusions.Read(rig.Paths.Lists.ExcludeIps).Count);
    }

    [LinuxFact]
    public async Task Exclusions_ReturnsTheCurrentLists()
    {
        await using ServiceRig rig = await ServiceRig.StartAsync(_env);
        await rig.Send(new ControlRequest(ControlCommands.SetExclusions, Hosts: ["a.example.com", "b.example.com"], Ips: ["192.0.2.0/24"]));

        ControlResponse r = await rig.Send("exclusions");

        Assert.True(r.Ok);
        Assert.Equal(["a.example.com", "b.example.com"], r.Hosts);
        Assert.Equal(["192.0.2.0/24"], r.Ips);
    }

    [LinuxFact]
    public async Task Strategies_ListsBuiltInsAndRootOwnedCustoms_HidesTheOthers()
    {
        await using ServiceRig rig = await ServiceRig.StartAsync(_env, rootOwned: ["mine"], beforeStart: p =>
        {
            Directory.CreateDirectory(p.StrategiesDir);
            File.WriteAllText(Path.Combine(p.StrategiesDir, "mine.args"), "--lua-desync=multisplit:pos=2\n");
            File.WriteAllText(Path.Combine(p.StrategiesDir, "planted.args"), "--lua-desync=multisplit:pos=3\n");
        });

        ControlResponse r = await rig.Send("strategies");

        Assert.True(r.Ok);
        Assert.Equal("auto", r.Current);
        Assert.Equal(
        [
            new StrategyItem("auto", "builtin"), new StrategyItem("fake-tls", "builtin"), new StrategyItem("split", "builtin"),
            new StrategyItem("split-stealth", "builtin"), new StrategyItem("aggressive", "builtin"),
            new StrategyItem("custom:mine", "custom"),
        ], r.Items);
    }

    [LinuxFact]
    public async Task Use_Custom_RunsItsArguments()
    {
        await using ServiceRig rig = await ServiceRig.StartAsync(_env, rootOwned: ["mine"], beforeStart: p =>
        {
            Directory.CreateDirectory(p.StrategiesDir);
            File.WriteAllText(Path.Combine(p.StrategiesDir, "mine.args"), "# mine\n--lua-desync=multisplit:pos=2\n--lua-desync=fake:blob=0x00:repeats=2\n");
        });

        ControlResponse r = await rig.Send("use", "custom:mine");

        Assert.True(r.Ok);
        Assert.Equal("custom:mine", r.Status!.Strategy);
        var part = new DesyncPart([], ["--lua-desync=multisplit:pos=2", "--lua-desync=fake:blob=0x00:repeats=2"]);
        Assert.True(WaitFor(() => _env.Args.SequenceEqual(rig.ExpectedArgv("custom:mine", part))), string.Join('|', _env.Args));
        Assert.Equal("custom:mine", ServiceState.Load(rig.Paths.StateIni).Strategy);
        Assert.Equal("custom:mine", (await rig.Send("strategies")).Current);
    }

    [LinuxFact]
    public async Task Use_CustomThatIsNotRootOwned_SaysWhy()
    {
        await using ServiceRig rig = await ServiceRig.StartAsync(_env, rootOwned: [], beforeStart: p =>
        {
            Directory.CreateDirectory(p.StrategiesDir);
            File.WriteAllText(Path.Combine(p.StrategiesDir, "planted.args"), "--lua-desync=multisplit:pos=3\n");
        });

        ControlResponse r = await rig.Send("use", "custom:planted");

        Assert.False(r.Ok);
        Assert.StartsWith("стратегия не прошла проверку: ", r.Error);
        Assert.Contains("должен принадлежать root", r.Error);
        Assert.Equal("auto", (await rig.Send("status")).Status!.Strategy);
    }

    [LinuxFact]
    public async Task Use_CustomWithAForbiddenOption_SaysWhich()
    {
        await using ServiceRig rig = await ServiceRig.StartAsync(_env, rootOwned: ["bad"], beforeStart: p =>
        {
            Directory.CreateDirectory(p.StrategiesDir);
            File.WriteAllText(Path.Combine(p.StrategiesDir, "bad.args"), "--lua-desync=multisplit\n--hostlist=/etc/shadow\n");
        });

        ControlResponse r = await rig.Send("use", "custom:bad");

        Assert.False(r.Ok);
        Assert.Contains("недопустимый параметр в стратегии: --hostlist", r.Error);
    }

    [LinuxFact]
    public async Task Status_ShowsACrashedChild_ThenItsRecovery()
    {
        await using ServiceRig rig = await ServiceRig.StartAsync(_env, start: false);
        _env.WriteCtl("crash_after_ms=500");
        await rig.Service.StartAsync();
        _env.WriteCtl();

        Assert.True(WaitFor(() => rig.Runner.Restarts >= 1));
        Assert.True(WaitFor(() => rig.Runner.State == RunnerState.Running && rig.Runner.Restarts >= 1));
        ZapretStatus status = (await rig.Send("status")).Status!;
        Assert.True(status.Restarts >= 1);
        Assert.Contains("crashing as asked", status.LastError);
    }

    private static void WriteAutoState(ServicePaths paths, string text)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(paths.AutoStateFile)!);
        File.WriteAllText(paths.AutoStateFile, text);
    }

    [LinuxFact]
    public async Task Status_WhileAuto_ListsWhatTheScriptReported()
    {
        await using ServiceRig rig = await ServiceRig.StartAsync(_env);
        WriteAutoState(rig.Paths, "b.example.com\t2\t1\na.example.com\t4\t0\n");

        ZapretStatus status = (await rig.Send("status")).Status!;

        Assert.Equal([new AutoHost("a.example.com", "aggressive", 0), new AutoHost("b.example.com", "split", 1)], status.Auto);
    }

    [LinuxFact]
    public async Task Status_WhileAuto_WithoutAFile_HasAnEmptyList()
    {
        await using ServiceRig rig = await ServiceRig.StartAsync(_env);

        ZapretStatus status = (await rig.Send("status")).Status!;

        Assert.NotNull(status.Auto);
        Assert.Empty(status.Auto);
    }

    [LinuxFact]
    public async Task Status_WithAnotherStrategy_OmitsAuto()
    {
        await using ServiceRig rig = await ServiceRig.StartAsync(_env);
        Assert.True((await rig.Send("use", "split")).Ok);
        WriteAutoState(rig.Paths, "a.example.com\t2\t0\n");

        ZapretStatus status = (await rig.Send("status")).Status!;

        Assert.Null(status.Auto);
        Assert.DoesNotContain("\"auto\":", ControlProtocol.Serialize(new ControlResponse(true, Status: status)));
    }

    [LinuxFact]
    public async Task Start_ClearsWhatAPreviousRunLeftBehind()
    {
        await using ServiceRig rig = await ServiceRig.StartAsync(_env, beforeStart: p => WriteAutoState(p, "stale.example.com\t3\t2\n"));

        Assert.False(File.Exists(rig.Paths.AutoStateFile));
        Assert.Empty((await rig.Send("status")).Status!.Auto!);
    }

    [LinuxFact]
    public async Task Start_WhileOff_LeavesTheFileAlone()
    {
        await using ServiceRig rig = await ServiceRig.StartAsync(_env, beforeStart: p =>
        {
            new ServiceState(false, "auto").Save(p.StateIni);
            WriteAutoState(p, "old.example.com\t2\t0\n");
        });

        Assert.Equal([new AutoHost("old.example.com", "split", 0)], (await rig.Send("status")).Status!.Auto);
    }

    [LinuxFact]
    public async Task Use_ClearsTheFile_ButNotWhenTheNewStrategyIsRefused()
    {
        await using ServiceRig rig = await ServiceRig.StartAsync(_env);
        WriteAutoState(rig.Paths, "a.example.com\t2\t0\n");

        _env.WriteCtl("dryrun_fail=1");
        Assert.False((await rig.Send("use", "split")).Ok);
        Assert.True(File.Exists(rig.Paths.AutoStateFile));   // the old run goes on, so does its state

        _env.WriteCtl();
        Assert.True((await rig.Send("use", "split")).Ok);
        Assert.False(File.Exists(rig.Paths.AutoStateFile));
    }

    // A state file that cannot be written: a directory sits where state.ini should be.
    private static void BreakStateFile(ServicePaths paths)
    {
        File.Delete(paths.StateIni);
        Directory.CreateDirectory(paths.StateIni);
    }

    [LinuxFact]
    public async Task On_WhenTheStateCannotBeSaved_SaysSo_AndTheOldStateStays()
    {
        await using ServiceRig rig = await ServiceRig.StartAsync(_env, beforeStart: p => new ServiceState(false, "auto").Save(p.StateIni));
        BreakStateFile(rig.Paths);

        ControlResponse r = await rig.Send("on");

        Assert.False(r.Ok);
        Assert.StartsWith("не удалось сохранить состояние", r.Error);
        Assert.False(r.Status!.Enabled);       // memory did not run ahead of the disk
        Assert.Null(rig.Runner.Pid);           // and nothing was started
        Assert.False((await rig.Send("status")).Status!.Enabled);
    }

    [LinuxFact]
    public async Task Use_WhenTheStateCannotBeSaved_PutsTheRememberedStrategyBack()
    {
        await using ServiceRig rig = await ServiceRig.StartAsync(_env);
        Assert.True(WaitFor(() => _env.Args.SequenceEqual(rig.ExpectedArgv("auto"))));
        BreakStateFile(rig.Paths);

        ControlResponse r = await rig.Send("use", "split");

        Assert.False(r.Ok);
        Assert.StartsWith("не удалось сохранить состояние", r.Error);
        Assert.Equal("auto", r.Status!.Strategy);
        Assert.Equal("running", r.Status.State);
        // What runs is what the state says: the new command line was replaced by the remembered one again.
        Assert.True(WaitFor(() => _env.Args.SequenceEqual(rig.ExpectedArgv("auto"))), string.Join('|', _env.Args));
    }

    [LinuxFact]
    public async Task Dispose_StopsTheChild()
    {
        ServiceRig rig = await ServiceRig.StartAsync(_env);
        int pid = rig.Runner.Pid!.Value;

        await rig.DisposeAsync();

        Assert.True(WaitFor(() => !FakeZapretEnv.IsAlive(pid)));
    }

    [LinuxFact]
    public async Task EveryCommand_ThatTheProtocolAccepts_IsHandled()
    {
        await using ServiceRig rig = await ServiceRig.StartAsync(_env);

        foreach (string cmd in new[] { "status", "on", "off", "on", "strategies", "exclusions" })
            Assert.True((await rig.Send(cmd)).Ok, cmd);
        Assert.True((await rig.Send("use", "fake-tls")).Ok);
        Assert.True((await rig.Send(new ControlRequest("set-exclusions", Hosts: [], Ips: []))).Ok);
    }
}
