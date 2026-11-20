using TgwsOne.Core.Zapret;

namespace TgwsOne.Core.Tests.Zapret;

[Collection(EnvCollection.Name)]
public class ZapretRunnerTests : IDisposable
{
    private readonly FakeZapretEnv _env = new();

    public void Dispose() => _env.Dispose();

    private static bool WaitFor(Func<bool> condition, int timeoutMs = 8000) => TestEnv.WaitFor(condition, timeoutMs);

    [LinuxFact]
    public async Task Apply_DryRunOk_Running_ArgsWritten()
    {
        await using ZapretRunner runner = _env.NewRunner();

        string? error = await runner.ApplyAsync(["--alpha", "beta=1"]);

        Assert.Null(error);
        Assert.Equal(RunnerState.Running, runner.State);
        Assert.NotNull(runner.Pid);
        Assert.NotNull(runner.Since);
        Assert.Equal(0, runner.Restarts);
        Assert.Null(runner.LastError);
        // The dry run (--dry-run --alpha beta=1) must not have been mistaken for the real run: no flag in the argv.
        Assert.True(WaitFor(() => _env.Args.SequenceEqual(["--alpha", "beta=1"])), string.Join('|', _env.Args));
        Assert.True(WaitFor(() => _env.Log.Contains("fake zapret running")), _env.Log);
        Assert.Contains("--- запуск", _env.Log);
    }

    [LinuxFact]
    public async Task Apply_DryRunFails_ErrorReturned_OldChildStillRunning()
    {
        await using ZapretRunner runner = _env.NewRunner();
        Assert.Null(await runner.ApplyAsync(["--a"]));
        int pid = runner.Pid!.Value;
        Assert.True(WaitFor(() => _env.Args.SequenceEqual(["--a"])));

        _env.WriteCtl("dryrun_fail=1");
        string? error = await runner.ApplyAsync(["--b"]);

        Assert.Contains("bad option", error);
        Assert.Equal(RunnerState.Running, runner.State);
        Assert.Equal(pid, runner.Pid);
        Assert.True(FakeZapretEnv.IsAlive(pid));
        Assert.Equal(["--a"], _env.Args);
        Assert.Null(runner.LastError);
    }

    [LinuxFact]
    public async Task Apply_DryRunFails_NothingRunning_IsErrorState()
    {
        await using ZapretRunner runner = _env.NewRunner();
        _env.WriteCtl("dryrun_fail=1");

        string? error = await runner.ApplyAsync(["--a"]);

        Assert.Contains("bad option", error);
        Assert.Equal(RunnerState.Error, runner.State);
        Assert.Contains("bad option", runner.LastError);
        Assert.Null(runner.Pid);
        Assert.Empty(_env.Args);
    }

    [LinuxFact]
    public async Task Apply_Again_ReplacesTheChild()
    {
        await using ZapretRunner runner = _env.NewRunner();
        Assert.Null(await runner.ApplyAsync(["--a"]));
        int first = runner.Pid!.Value;

        Assert.Null(await runner.ApplyAsync(["--b"]));

        Assert.NotEqual(first, runner.Pid);
        Assert.True(WaitFor(() => !FakeZapretEnv.IsAlive(first)));
        Assert.True(WaitFor(() => _env.Args.SequenceEqual(["--b"])));
        Assert.Equal(0, runner.Restarts);
        Assert.Equal(RunnerState.Running, runner.State);
    }

    [LinuxFact]
    public async Task Crash_RestartsWithBackoff()
    {
        await using ZapretRunner runner = _env.NewRunner();
        _env.WriteCtl("crash_after_ms=500");   // longer than the startup grace: a crash of a child that was up
        Assert.Null(await runner.ApplyAsync(["--a"]));
        int first = runner.Pid!.Value;
        _env.WriteCtl();                       // the restarted child must stay up

        Assert.True(WaitFor(() => runner.Restarts >= 1), "no crash seen");
        Assert.True(WaitFor(() => runner.State == RunnerState.Running && runner.Pid is { } p && p != first), $"state {runner.State}");
        Assert.Contains("crashing as asked", runner.LastError);
        Assert.True(FakeZapretEnv.IsAlive(runner.Pid!.Value));
        Assert.Contains("--- завершился с кодом 2", _env.Log);
    }

    [LinuxFact]
    public async Task Crash_LastError_LeadsWithTheExitCode_ThenTheOutput()
    {
        await using ZapretRunner runner = _env.NewRunner();
        _env.WriteCtl("crash_after_ms=500");
        Assert.Null(await runner.ApplyAsync(["--a"]));
        _env.WriteCtl();

        Assert.True(WaitFor(() => runner.Restarts >= 1));

        string[] lines = runner.LastError!.Split('\n');
        Assert.Equal("завершился с кодом 2", lines[0]);
        Assert.Contains("fake zapret: crashing as asked", lines.Skip(1));
    }

    [LinuxFact]
    public async Task KilledBySignal_LastErrorSaysWhichSignal()
    {
        await using ZapretRunner runner = _env.NewRunner();
        Assert.Null(await runner.ApplyAsync(["--a"]));
        int pid = runner.Pid!.Value;

        Assert.True(LibC.Kill(pid, 9));

        Assert.True(WaitFor(() => runner.Restarts >= 1));
        Assert.Equal("завершился с кодом 137 (сигнал 9)", runner.LastError!.Split('\n')[0]);
        Assert.True(WaitFor(() => runner.State == RunnerState.Running && runner.Pid != pid), $"state {runner.State}");
    }

    [LinuxFact]
    public async Task RepeatedCrashes_StateError_LastErrorHasOutput()
    {
        await using ZapretRunner runner = _env.NewRunner();
        _env.WriteCtl("crash_after_ms=300");
        Assert.Null(await runner.ApplyAsync(["--a"]));

        Assert.True(WaitFor(() => runner.State == RunnerState.Error), $"state {runner.State}, restarts {runner.Restarts}");
        Assert.Contains("crashing as asked", runner.LastError);
        Assert.True(runner.Restarts >= 3);
        Assert.Contains("ошибка", _env.ServiceLogText);

        // The error state still retries: fix the cause and it comes back by itself.
        _env.WriteCtl();
        Assert.True(WaitFor(() => runner.State == RunnerState.Running && runner.Pid != null), $"state {runner.State}");
    }

    [LinuxFact]
    public async Task Apply_ChildDiesAtOnce_NothingBefore_ReturnsTheErrorAndKeepsTrying()
    {
        await using ZapretRunner runner = _env.NewRunner();

        string? error = await runner.ApplyAsync(["--die-at-start"]);

        Assert.Contains("dying at start as asked", error);
        Assert.Contains(runner.State, new[] { RunnerState.Backoff, RunnerState.Error });
        Assert.Null(runner.Pid);
        Assert.True(WaitFor(() => runner.Restarts >= 3), $"restarts {runner.Restarts}");
    }

    [LinuxFact]
    public async Task Apply_ChildDiesAtOnce_PreviousIsPutBack()
    {
        await using ZapretRunner runner = _env.NewRunner();
        Assert.Null(await runner.ApplyAsync(["--ok"]));
        int first = runner.Pid!.Value;

        string? error = await runner.ApplyAsync(["--die-at-start"]);

        Assert.Contains("dying at start as asked", error);
        Assert.Equal(RunnerState.Running, runner.State);
        Assert.NotNull(runner.Pid);
        Assert.NotEqual(first, runner.Pid);
        Assert.True(FakeZapretEnv.IsAlive(runner.Pid!.Value));
        Assert.True(WaitFor(() => _env.Args.SequenceEqual(["--ok"])), string.Join('|', _env.Args));
        Assert.Equal(0, runner.Restarts);   // refusing the new command line is not a crash
    }

    [LinuxFact]
    public async Task Apply_Success_ClearsTheMessageFromAnEarlierCrash()
    {
        await using ZapretRunner runner = _env.NewRunner();
        _env.WriteCtl("crash_after_ms=500");
        Assert.Null(await runner.ApplyAsync(["--a"]));
        _env.WriteCtl();
        Assert.True(WaitFor(() => runner.Restarts >= 1 && runner.State == RunnerState.Running), $"state {runner.State}");
        Assert.NotNull(runner.LastError);   // the supervisor's own recovery keeps the last crash on record

        Assert.Null(await runner.ApplyAsync(["--b"]));

        Assert.Null(runner.LastError);      // a deliberate, successful start does not
    }

    [LinuxFact]
    public async Task Apply_CancelledDuringTheStartupGrace_Throws_InsteadOfReportingSuccess()
    {
        var policy = FakeZapretEnv.FastPolicy with { StartupGrace = TimeSpan.FromSeconds(4) };
        await using ZapretRunner runner = _env.NewRunner(policy: policy);
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(1500));   // after the dry run and the spawn

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => runner.ApplyAsync(["--a"], cts.Token));

        Assert.NotEqual(RunnerState.Running, runner.State);   // the grace period had not passed
        await runner.StopAsync();                              // and what was started is cleaned up the normal way
        Assert.Equal(RunnerState.Disabled, runner.State);
        Assert.Null(runner.Pid);
    }

    [LinuxFact]
    public async Task Stop_KillsChild_StateDisabled()
    {
        await using ZapretRunner runner = _env.NewRunner();
        Assert.Null(await runner.ApplyAsync(["--a"]));
        int pid = runner.Pid!.Value;

        await runner.StopAsync();

        Assert.Equal(RunnerState.Disabled, runner.State);
        Assert.Null(runner.Pid);
        Assert.Null(runner.Since);
        Assert.Null(runner.LastError);
        Assert.True(WaitFor(() => !FakeZapretEnv.IsAlive(pid)));
        Assert.Equal(0, runner.Restarts);   // a requested stop is not a crash, and nothing restarts it
        await Task.Delay(400);
        Assert.Null(runner.Pid);
        Assert.Equal(RunnerState.Disabled, runner.State);
    }

    [LinuxFact]
    public async Task Stop_WhileInBackoff_CancelsTheRestart()
    {
        await using ZapretRunner runner = _env.NewRunner();
        Assert.NotNull(await runner.ApplyAsync(["--die-at-start"]));   // dies at once; a restart is now pending
        await runner.StopAsync();
        int restarts = runner.Restarts;

        await Task.Delay(500);

        Assert.Equal(RunnerState.Disabled, runner.State);
        Assert.Null(runner.Pid);
        Assert.Equal(restarts, runner.Restarts);
    }

    [LinuxFact]
    public async Task ReloadLists_SendsSighup()
    {
        await using ZapretRunner runner = _env.NewRunner();
        Assert.Null(await runner.ApplyAsync(["--a"]));
        int pid = runner.Pid!.Value;
        Assert.True(WaitFor(() => _env.Log.Contains("fake zapret running")));   // the handler is registered by then

        await runner.ReloadListsAsync();
        Assert.True(WaitFor(() => _env.Hups >= 1), "no HUP seen");
        await runner.ReloadListsAsync();
        Assert.True(WaitFor(() => _env.Hups >= 2), "second HUP missing");

        Assert.Equal(pid, runner.Pid);
        Assert.Equal(RunnerState.Running, runner.State);
    }

    [LinuxFact]
    public async Task ReloadLists_WhenNothingRuns_DoesNothing()
    {
        await using ZapretRunner runner = _env.NewRunner();

        await runner.ReloadListsAsync();

        Assert.Equal(0, _env.Hups);
        Assert.Equal(RunnerState.Disabled, runner.State);
    }

    [LinuxFact]
    public async Task Dispose_LeavesNoChild()
    {
        ZapretRunner runner = _env.NewRunner();
        Assert.Null(await runner.ApplyAsync(["--a"]));
        int pid = runner.Pid!.Value;

        await runner.DisposeAsync();

        Assert.True(WaitFor(() => !FakeZapretEnv.IsAlive(pid)));
        await Assert.ThrowsAsync<ObjectDisposedException>(() => runner.ApplyAsync(["--b"]));
    }

    [LinuxFact]
    public async Task Nft_AppliedOnceBeforeTheFirstStart_AndRemovedOnStop()
    {
        var nftRunner = new FakeRunner();
        var nft = new NftManager(nftRunner, new FakeSysctl());
        await using ZapretRunner runner = _env.NewRunner(nft);

        Assert.Null(await runner.ApplyAsync(["--a"]));
        Assert.Null(await runner.ApplyAsync(["--b"]));

        RunnerCall apply = Assert.Single(nftRunner.Calls);
        Assert.Equal(["-f", "-"], apply.Args);
        Assert.Contains("table inet tgwsone", apply.Stdin);

        await runner.StopAsync();

        Assert.Equal(2, nftRunner.Calls.Count);
        Assert.Equal(["delete", "table", "inet", "tgwsone"], nftRunner.Calls[1].Args);
    }

    [LinuxFact]
    public async Task Nft_DryRunFailure_LeavesTheTableAlone()
    {
        var nftRunner = new FakeRunner();
        await using ZapretRunner runner = _env.NewRunner(new NftManager(nftRunner, new FakeSysctl()));
        _env.WriteCtl("dryrun_fail=1");

        Assert.NotNull(await runner.ApplyAsync(["--a"]));

        Assert.Empty(nftRunner.Calls);
    }

    [LinuxFact]
    public async Task Nft_Failure_ReturnsItsOutput_AndStartsNothing()
    {
        var nftRunner = new FakeRunner { Respond = _ => (1, "Error: Operation not permitted") };
        await using ZapretRunner runner = _env.NewRunner(new NftManager(nftRunner, new FakeSysctl()));

        string? error = await runner.ApplyAsync(["--a"]);

        Assert.Contains("Operation not permitted", error);
        Assert.Equal(RunnerState.Error, runner.State);
        Assert.Contains("Operation not permitted", runner.LastError);
        Assert.Null(runner.Pid);
        Assert.Empty(_env.Args);
    }

    [LinuxFact]
    public async Task MissingExecutable_IsAnError()
    {
        await using ZapretRunner runner = _env.NewRunner(exe: _env.Combine("no-such-zapret"));

        string? error = await runner.ApplyAsync(["--a"]);

        Assert.False(string.IsNullOrWhiteSpace(error));
        Assert.Equal(RunnerState.Error, runner.State);
        Assert.Null(runner.Pid);
    }

    [LinuxFact]
    public async Task ChildOutput_GoesToTheLogFile_WithTimestamps()
    {
        await using ZapretRunner runner = _env.NewRunner();
        Assert.Null(await runner.ApplyAsync(["--a"]));

        Assert.True(WaitFor(() => _env.Log.Contains("fake zapret running")), _env.Log);

        Assert.Matches(@"\d{4}-\d\d-\d\d \d\d:\d\d:\d\d  fake zapret running", _env.Log);
    }
}
