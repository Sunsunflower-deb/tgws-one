using System.ComponentModel;
using System.Diagnostics;
using System.Text;
using TgwsOne.Core.Zapret.Windows;

namespace TgwsOne.Core.Zapret;

public enum RunnerState { Disabled, Starting, Running, Backoff, Error }

/// <summary>
/// How crashes are handled: the wait before each restart, when a run counts as stable, how many exits within which
/// window make it an error (retried at <see cref="ErrorRetry"/> from then on), and how long a fresh child must
/// stay alive before it counts as started (<see cref="StartupGrace"/>, 1 s unless set).
/// </summary>
public sealed record BackoffPolicy(IReadOnlyList<TimeSpan> Delays, TimeSpan SteadyAfter, int MaxCrashes, TimeSpan CrashWindow,
                                   TimeSpan ErrorRetry, TimeSpan? StartupGrace = null)
{
    public static BackoffPolicy Default { get; } = new(
        [TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(15), TimeSpan.FromSeconds(60), TimeSpan.FromMinutes(5)],
        SteadyAfter: TimeSpan.FromMinutes(10), MaxCrashes: 5, CrashWindow: TimeSpan.FromMinutes(5), ErrorRetry: TimeSpan.FromMinutes(5));

    internal TimeSpan Grace => StartupGrace ?? TimeSpan.FromSeconds(1);
}

/// <summary>
/// Owns the winws2/nfqws2 child: starts it with a command line that passed <c>--dry-run</c>, restarts it when it dies
/// (with backoff), and puts it away on request. All state changes happen under one lock; an epoch number voids
/// restart timers and exit watchers that belong to a command line that has since been replaced or stopped.
/// </summary>
public sealed class ZapretRunner : IAsyncDisposable
{
    private const int TailLines = 10;

    private readonly string _exe;
    private readonly AppLog _log;        // the service log: what the runner decides
    private readonly AppLog _childLog;   // zapret.log: what the child prints
    private readonly NftManager? _nft;
    private readonly BackoffPolicy _policy;
    private readonly ProcessCommandRunner _dryRun = new();
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly CancellationTokenSource _lifetime = new();
    private readonly Queue<DateTimeOffset> _crashTimes = new();
    private IDisposable? _job;   // Windows: the kill-on-close job object

    private IReadOnlyList<string>? _argv;   // what should be running; null while disabled
    private Child? _child;
    private int _epoch;
    private int _attempt;
    private bool _nftApplied;
    private volatile bool _disposed;

    // Written under the gate, read without it by the status report.
    private volatile RunnerState _state = RunnerState.Disabled;
    private volatile string? _lastError;
    private int _pid;
    private long _sinceTicks;
    private int _restarts;

    public ZapretRunner(string exe, string logFile, AppLog log, NftManager? nft, BackoffPolicy? policy = null)
    {
        _exe = exe;
        _log = log;
        _childLog = new AppLog(logFile);
        _nft = nft;
        _policy = policy ?? BackoffPolicy.Default;
    }

    public RunnerState State => _state;
    public int? Pid => _pid == 0 ? null : _pid;
    /// <summary>When the current child started; null while none is running.</summary>
    public DateTimeOffset? Since => _sinceTicks == 0 ? null : new DateTimeOffset(Interlocked.Read(ref _sinceTicks), TimeSpan.Zero);
    /// <summary>How many times the child died on its own since the service started.</summary>
    public int Restarts => _restarts;
    /// <summary>The dry-run output, or the tail of the child's output after its last crash.</summary>
    public string? LastError => _lastError;

    /// <summary>
    /// Makes <paramref name="argv"/> the running command line. It is checked with <c>--dry-run</c> first: if that fails, the
    /// error comes back and whatever ran before keeps running. A child that dies within the startup grace (a Lua error
    /// the dry run can't see) is an error too, and the previous command line, if there was one, is put back.
    /// </summary>
    /// <returns>An error text, or null when the child is up.</returns>
    public async Task<string?> ApplyAsync(IReadOnlyList<string> argv, CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, _lifetime.Token);
        await _gate.WaitAsync(linked.Token);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);

            string? bad = await DryRunAsync(argv, linked.Token);
            if (bad != null)
            {
                _log.Write($"zapret: проверка параметров не прошла: {bad}");
                if (_argv == null)
                {
                    _state = RunnerState.Error;
                    _lastError = bad;
                }
                return bad;
            }

            if (_nft != null && !_nftApplied)
            {
                try
                {
                    await _nft.ApplyAsync(linked.Token);
                    _nftApplied = true;
                }
                catch (InvalidOperationException ex)
                {
                    _log.Write($"zapret: {ex.Message}");
                    if (_argv == null)
                    {
                        _state = RunnerState.Error;
                        _lastError = ex.Message;
                    }
                    return ex.Message;
                }
            }

            IReadOnlyList<string>? previous = _argv;
            _epoch++;
            await KillCurrentAsync();
            _argv = argv;
            _attempt = 0;
            _crashTimes.Clear();

            string? failure = await LaunchAsync(argv, linked.Token);
            if (failure == null)
            {
                _lastError = null;   // a deliberate start that worked: the message from an earlier crash no longer applies
                return null;
            }

            _log.Write($"zapret: не запустился: {failure}");
            if (previous != null)
            {
                _argv = previous;
                string? back = await LaunchAsync(previous, linked.Token);
                if (back != null) RegisterCrash(back, DateTimeOffset.UtcNow);
            }
            else
            {
                RegisterCrash(failure, DateTimeOffset.UtcNow);   // nothing to go back to: keep trying, with backoff
            }
            return failure;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Kills the child, removes the nft table, forgets the command line. State becomes Disabled.</summary>
    public async Task StopAsync()
    {
        await _gate.WaitAsync();
        try
        {
            await StopLockedAsync();
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// The list files changed. Linux: SIGHUP makes nfqws2 re-read them. Windows has no such signal, so the child is
    /// restarted. Nothing is done while no child runs: the next start reads the files anyway.
    /// </summary>
    public async Task ReloadListsAsync()
    {
        if (_disposed) return;
        await _gate.WaitAsync(_lifetime.Token);
        try
        {
            if (_argv == null || _child == null) return;
            if (!OperatingSystem.IsWindows())
            {
                if (!LibC.Kill(_child.Pid, LibC.SigHup)) _log.Write("zapret: не удалось послать SIGHUP");
                return;
            }

            _epoch++;
            await KillCurrentAsync();
            string? failure = await LaunchAsync(_argv, _lifetime.Token);
            if (failure != null) RegisterCrash(failure, DateTimeOffset.UtcNow);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _lifetime.Cancel();   // voids pending restarts and unblocks an Apply that is waiting
        await _gate.WaitAsync();
        try
        {
            _disposed = true;
            await StopLockedAsync();
        }
        finally
        {
            _gate.Release();
        }
        _job?.Dispose();
    }

    private async Task StopLockedAsync()
    {
        _epoch++;
        _argv = null;
        _attempt = 0;
        _crashTimes.Clear();
        await KillCurrentAsync();
        if (_nft != null && _nftApplied)
        {
            _nftApplied = false;
            try { await _nft.RemoveAsync(); }
            catch (InvalidOperationException ex) { _log.Write($"zapret: {ex.Message}"); }
        }
        _state = RunnerState.Disabled;
        _lastError = null;
    }

    private async Task KillCurrentAsync()
    {
        if (_child is { } child)
        {
            _child = null;
            await KillAsync(child);
        }
        ClearProcessInfo();
    }

    private async Task<string?> DryRunAsync(IReadOnlyList<string> argv, CancellationToken ct)
    {
        (int code, string output) = await _dryRun.RunAsync(_exe, ["--dry-run", .. argv], null, ct);
        if (code == 0) return null;
        string text = LastLines(output, TailLines);
        return text.Length > 0 ? text : $"проверка параметров завершилась с кодом {code}";
    }

    /// <summary>
    /// Starts <paramref name="argv"/> as the live child and waits for the startup grace. Null: it is still alive and the
    /// state is Running. Otherwise the text of why it isn't (it was cleaned up, the caller decides what follows).
    /// </summary>
    private async Task<string?> LaunchAsync(IReadOnlyList<string> argv, CancellationToken ct)
    {
        _state = RunnerState.Starting;
        Child child;
        try
        {
            child = Spawn(argv);
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or IOException)
        {
            return $"не удалось запустить {_exe}: {ex.Message}";
        }
        _child = child;
        _pid = child.Pid;
        Interlocked.Exchange(ref _sinceTicks, child.Started.UtcTicks);
        _ = WatchAsync(child, _epoch);

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        Task winner = await Task.WhenAny(child.Exited, Task.Delay(_policy.Grace, cts.Token));
        await cts.CancelAsync();
        // A cancelled wait also "completes": that is the service stopping, not the grace period passing.
        ct.ThrowIfCancellationRequested();
        if (winner != child.Exited)
        {
            _state = RunnerState.Running;
            return null;
        }

        _child = null;
        ClearProcessInfo();
        return CrashText(child);
    }

    // Waits for the child to end. If it was not on purpose (the child is still the current one), it is a crash.
    private async Task WatchAsync(Child child, int epoch)
    {
        await child.Exited;
        try { await _gate.WaitAsync(_lifetime.Token); }
        catch (OperationCanceledException) { return; }
        try
        {
            if (_disposed || epoch != _epoch || _child != child) return;
            _child = null;
            ClearProcessInfo();
            RegisterCrash(CrashText(child), child.Started);
        }
        finally
        {
            _gate.Release();
        }
    }

    // Counts a crash and decides when to try again. Called with the gate held.
    private void RegisterCrash(string text, DateTimeOffset startedAt)
    {
        _lastError = text;
        Interlocked.Increment(ref _restarts);
        DateTimeOffset now = DateTimeOffset.UtcNow;
        if (now - startedAt >= _policy.SteadyAfter)
        {
            _attempt = 0;
            _crashTimes.Clear();
        }
        _crashTimes.Enqueue(now);
        while (_crashTimes.Peek() < now - _policy.CrashWindow) _crashTimes.Dequeue();

        bool error = _crashTimes.Count >= _policy.MaxCrashes || _attempt >= _policy.Delays.Count;
        TimeSpan delay = error ? _policy.ErrorRetry : _policy.Delays[_attempt];
        _attempt++;
        _state = error ? RunnerState.Error : RunnerState.Backoff;
        _log.Write($"zapret: {(error ? "ошибка" : "сбой")}, повтор через {delay.TotalSeconds:0.#} с: {FirstLine(text)}");
        _ = RestartAfterAsync(delay, _epoch);
    }

    private async Task RestartAfterAsync(TimeSpan delay, int epoch)
    {
        try
        {
            await Task.Delay(delay, _lifetime.Token);
            await _gate.WaitAsync(_lifetime.Token);
        }
        catch (OperationCanceledException)
        {
            return;
        }
        try
        {
            if (_disposed || epoch != _epoch || _argv == null || _child != null) return;
            _log.Write("zapret: перезапуск");
            string? failure = await LaunchAsync(_argv, _lifetime.Token);
            if (failure != null) RegisterCrash(failure, DateTimeOffset.UtcNow);
        }
        catch (OperationCanceledException) { /* disposing */ }
        finally
        {
            _gate.Release();
        }
    }

    private void ClearProcessInfo()
    {
        _pid = 0;
        Interlocked.Exchange(ref _sinceTicks, 0);
    }

    private static string CrashText(Child child)
    {
        string tail = child.Tail();
        return tail.Length > 0 ? tail : $"завершился с кодом {child.ExitCode}";
    }

    private async Task KillAsync(Child child)
    {
        if (!child.Exited.IsCompleted)
        {
            try { child.Process.Kill(entireProcessTree: true); }
            catch (Exception ex) when (ex is InvalidOperationException or Win32Exception or NotSupportedException) { /* already gone */ }
        }
        try { await child.Exited.WaitAsync(TimeSpan.FromSeconds(5)); }
        catch (TimeoutException) { _log.Write($"zapret: процесс {child.Pid} не завершился после остановки"); }
    }

    private Child Spawn(IReadOnlyList<string> argv)
    {
        var psi = new ProcessStartInfo(_exe)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        foreach (string a in argv) psi.ArgumentList.Add(a);

        Process process = Process.Start(psi) ?? throw new InvalidOperationException("процесс не запустился");
        if (OperatingSystem.IsWindows()) AssignToJob(process);

        var child = new Child(process);
        _childLog.Write($"--- запуск {_exe} (pid {child.Pid})");
        Task pumps = Task.WhenAll(Pump(process.StandardOutput, child), Pump(process.StandardError, child));
        child.Exited = Task.Run(async () =>
        {
            await process.WaitForExitAsync();
            try { await pumps.WaitAsync(TimeSpan.FromSeconds(2)); }
            catch { /* a stray grandchild still holds the pipe: use what has arrived */ }
            child.ExitCode = process.ExitCode;
            process.Dispose();
            _childLog.Write($"--- завершился с кодом {child.ExitCode}");
        });
        return child;
    }

    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    private void AssignToJob(Process process)
    {
        try
        {
            var job = (KillOnCloseJob)(_job ??= new KillOnCloseJob());
            job.Assign(process);
        }
        catch (Win32Exception ex)
        {
            _log.Write($"zapret: не удалось привязать процесс к заданию: {ex.Message}");
        }
    }

    private async Task Pump(StreamReader reader, Child child)
    {
        try
        {
            string? line;
            while ((line = await reader.ReadLineAsync()) != null)
            {
                child.AddLine(line);
                _childLog.Write(line);
            }
        }
        catch (IOException) { /* the pipe closed under us */ }
    }

    private static string LastLines(string text, int count)
    {
        string[] lines = text.Replace("\r\n", "\n").Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return string.Join('\n', lines.Skip(Math.Max(0, lines.Length - count)));
    }

    private static string FirstLine(string text)
    {
        int nl = text.IndexOf('\n');
        return nl < 0 ? text : text[..nl];
    }

    private sealed class Child(Process process)
    {
        private readonly Queue<string> _tail = new();

        public Process Process { get; } = process;
        public int Pid { get; } = process.Id;
        public DateTimeOffset Started { get; } = DateTimeOffset.UtcNow;
        /// <summary>Completes once the process has ended and its output has been collected.</summary>
        public Task Exited { get; set; } = Task.CompletedTask;
        public int ExitCode { get; set; }

        public void AddLine(string line)
        {
            lock (_tail)
            {
                _tail.Enqueue(line);
                while (_tail.Count > TailLines) _tail.Dequeue();
            }
        }

        public string Tail()
        {
            lock (_tail) return string.Join('\n', _tail);
        }
    }
}
