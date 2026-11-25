using System.ComponentModel;
using System.Diagnostics;
using System.Net;
using System.Text;

namespace TgwsOne.Core;

public enum StartOutcome { Started, AlreadyRunning, PortSwitched, PortExhausted, EngineMissing, EngineExited, Timeout }

/// <param name="Port">The port the engine listens on (or, on failure, the one that was tried).</param>
/// <param name="OldPort">Set after a port switch: the configured port that turned out to be busy.</param>
/// <param name="ExitCode">Set for <see cref="StartOutcome.EngineExited"/> when the process ran and exited.</param>
/// <param name="Output">The tail of the engine's console output, for <see cref="StartOutcome.EngineExited"/>.</param>
public sealed record StartResult(StartOutcome Outcome, int Port, int? OldPort = null, int? ExitCode = null, string Output = "")
{
    public bool Ok => Outcome is StartOutcome.Started or StartOutcome.AlreadyRunning or StartOutcome.PortSwitched;
}

public enum StopOutcome { Stopped, NotRunning }

public sealed record ProxyStatus(bool Running, bool Listening, int Port, bool PortBusyByOther, int? Pid);

/// <summary>
/// Starts, stops and inspects the proxy engine. Holds no state of its own that matters: the INI and the pid
/// file are re-read on every call, so a CLI process and a GUI process can control the same engine.
/// </summary>
public sealed class ProxyController : IProxyEngineControl
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(250);
    private static readonly TimeSpan LockPollInterval = TimeSpan.FromMilliseconds(100);
    private static readonly TimeSpan StopTimeout = TimeSpan.FromSeconds(5);
    private const int OutputTailLines = 20;

    private readonly AppPaths _paths;
    private readonly AppLog _log;
    private readonly TimeSpan _startTimeout;
    private readonly TimeSpan _lockTimeout;
    private readonly bool _useControlChannel;

    // The engine this instance started last. Holding the Process lets .NET reap it when it exits,
    // so a long-lived host (the GUI) does not collect a zombie per restart.
    private Process? _child;

    /// <param name="startTimeout">How long to wait for the engine to accept connections (default 15 s).</param>
    /// <param name="lockTimeout">How long to wait for another starter to finish (default 20 s).</param>
    /// <param name="useControlChannel">Ask a running window to control its in-process engine before touching any
    /// process. The in-process host's own fallback turns this off so it cannot call back into itself.</param>
    public ProxyController(AppPaths paths, AppLog log, TimeSpan? startTimeout = null, TimeSpan? lockTimeout = null,
        bool useControlChannel = true)
    {
        _paths = paths;
        _log = log;
        _startTimeout = startTimeout ?? TimeSpan.FromSeconds(15);
        _lockTimeout = lockTimeout ?? TimeSpan.FromSeconds(20);
        _useControlChannel = useControlChannel;
    }

    /// <summary>
    /// Starts the engine. The whole sequence runs under an exclusive lock file, so two starters (two CLI
    /// processes, or the CLI and the GUI) cannot both launch an engine: the one that waits then finds
    /// the engine running and gets <see cref="StartOutcome.AlreadyRunning"/>.
    /// </summary>
    /// <exception cref="TimeoutException">Another starter held the lock for longer than the lock timeout.</exception>
    public async Task<StartResult> StartAsync(CancellationToken ct = default)
    {
        // The window, when it is running, owns the engine: ask it first. This is before the start lock on purpose —
        // the window's handler takes that same lock, and holding it while waiting for the answer would deadlock.
        if (_useControlChannel)
        {
            if (await ControlAsync(EngineControlCommands.Start, _startTimeout + TimeSpan.FromSeconds(5), ct)
                    .ConfigureAwait(false) is { } viaWindow)
                return ToStartResult(viaWindow);

            // No answer to "start", but maybe the window is there and still finishing it. Starting a second engine
            // now would race it for the port.
            if (await ControlAsync(EngineControlCommands.Status, TimeSpan.FromMilliseconds(500), ct)
                    .ConfigureAwait(false) is { Ok: true } status)
            {
                return status.Running || status.Listening
                    ? new StartResult(StartOutcome.AlreadyRunning, status.Port)
                    : new StartResult(StartOutcome.Timeout, status.Port);
            }
        }

        await using FileStream startLock = await AcquireStartLockAsync(ct).ConfigureAwait(false);
        return await StartLockedAsync(ct).ConfigureAwait(false);
    }

    private async Task<EngineControlResponse?> ControlAsync(string command, TimeSpan timeout, CancellationToken ct)
        => _useControlChannel
            ? await EngineControlClient.SendAsync(_paths, new EngineControlRequest(command), timeout, ct)
                .ConfigureAwait(false)
            : null;

    private StartResult ToStartResult(EngineControlResponse r)
        => new(Enum.TryParse(r.Outcome, out StartOutcome outcome) ? outcome : StartOutcome.EngineExited,
            r.Port, r.OldPort, r.ExitCode,
            r.Output.Length > 0 ? r.Output : r.Error ?? "");

    // FileShare.None is an exclusive lock: flock on Unix, a sharing violation on Windows. The OS drops it with
    // the process, so a crashed starter cannot leave it stuck. The descriptor is close-on-exec, so the engine
    // we spawn does not inherit it. The file itself stays on disk; deleting it would reopen the race.
    private async Task<FileStream> AcquireStartLockAsync(CancellationToken ct)
    {
        PrivateDirectory.Ensure(_paths.RunDir);
        var clock = Stopwatch.StartNew();
        while (true)
        {
            try
            {
                return new FileStream(_paths.StartLockFile, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None, bufferSize: 1);
            }
            catch (IOException) when (clock.Elapsed < _lockTimeout)
            {
                await Task.Delay(LockPollInterval, ct).ConfigureAwait(false);
            }
            catch (IOException ex)
            {
                _log.Write("другой запуск прокси ещё не завершился");
                throw new TimeoutException($"другой запуск прокси не завершился за {_lockTimeout.TotalSeconds:0} с", ex);
            }
        }
    }

    private async Task<StartResult> StartLockedAsync(CancellationToken ct)
    {
        IniFile ini = IniFile.Load(_paths.Ini);

        // 1. Already ours and running?
        if (EngineRecord.Read(_paths.PidFile) is { } existing && existing.IsAlive())
        {
            _log.Write("прокси уже запущен");
            return new StartResult(StartOutcome.AlreadyRunning, ProxySettingsStore.Load(ini).Port);
        }

        // 2-3. Secret and settings.
        ProxySettingsStore.EnsureSecret(ini, _log);
        ProxySettings s = ProxySettingsStore.Load(ini);
        if (!File.Exists(_paths.Engine))
        {
            _log.Write("движок не найден: " + _paths.Engine);
            return new StartResult(StartOutcome.EngineMissing, s.Port);
        }

        // 4. Port: if busy, look at the next 99 and remember the choice.
        int? oldPort = null;
        if (await Task.Run(() => PortProbe.IsBusy(s.Host, s.Port), ct).ConfigureAwait(false))
        {
            int first = s.Port + 1;
            if (await Task.Run(() => PortProbe.FindFree(s.Host, first, 99), ct).ConfigureAwait(false) is not { } free)
            {
                _log.Write($"порт {s.Port} занят, свободных портов рядом нет — смените port в настройках");
                return new StartResult(StartOutcome.PortExhausted, s.Port);
            }
            _log.Write($"порт {s.Port} занят — перехожу на {free}");
            ini.Set("proxy", "port", free.ToString());
            ini.Save();
            oldPort = s.Port;
            s = s with { Port = free };
        }

        // 5. Launch detached and record it at once, so a crash of this process cannot orphan the engine unnoticed.
        IReadOnlyList<string> argv = EngineCommand.Build(s, _paths);
        _log.Write("запускаю движок: " + _paths.Engine);
        Process p;
        try
        {
            string workDir = Path.GetDirectoryName(Path.GetFullPath(_paths.Engine))!;
            p = DetachedLauncher.Start(_paths.Engine, argv, _paths.EngineConsoleLog, workDir);
        }
        catch (Exception ex) when (ex is Win32Exception or IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            _log.Write("не удалось запустить прокси: " + ex.Message);
            return new StartResult(StartOutcome.EngineExited, s.Port, oldPort, Output: ex.Message);
        }
        _child = p;

        try
        {
            EngineRecord.ForProcess(p, _paths.Engine, EngineCommand.Hash(argv)).Write(_paths.PidFile);
        }
        catch (InvalidOperationException)
        {
            // It exited before its start stamp could be read: nothing to record, the loop below reports the exit.
        }
        _log.Write("движок запущен, PID " + p.Id);

        // 6. Wait until it accepts connections, dies, or the timeout passes.
        IPAddress connectTo = TelegramLink.ConnectAddress(s.Host);
        var clock = Stopwatch.StartNew();
        try
        {
            while (clock.Elapsed < _startTimeout)
            {
                if (p.HasExited)
                {
                    EngineRecord.Delete(_paths.PidFile);
                    int code = p.ExitCode;
                    _log.Write($"движок завершился с кодом {code}, вывод: {_paths.EngineConsoleLog}");
                    return new StartResult(StartOutcome.EngineExited, s.Port, oldPort, code, ConsoleTail());
                }
                if (await PortProbe.CanConnectAsync(connectTo, s.Port, 200, ct).ConfigureAwait(false))
                {
                    _log.Write($"прокси слушает {s.Host}:{s.Port}");
                    return new StartResult(oldPort is null ? StartOutcome.Started : StartOutcome.PortSwitched, s.Port, oldPort);
                }
                await Task.Delay(PollInterval, ct).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            await KillAsync(p).ConfigureAwait(false);
            EngineRecord.Delete(_paths.PidFile);
            throw;
        }

        await KillAsync(p).ConfigureAwait(false);
        EngineRecord.Delete(_paths.PidFile);
        _log.Write($"прокси не поднялся: порт {s.Port} не слушается. Смотрите proxy.log");
        return new StartResult(StartOutcome.Timeout, s.Port, oldPort);
    }

    public async Task<StopOutcome> StopAsync(CancellationToken ct = default)
    {
        if (await ControlAsync(EngineControlCommands.Stop, TimeSpan.FromSeconds(10), ct).ConfigureAwait(false) is { } viaWindow)
        {
            if (!viaWindow.Ok) throw new InvalidOperationException(viaWindow.Error ?? "окно не остановило прокси");
            return Enum.TryParse(viaWindow.Outcome, out StopOutcome result) ? result : StopOutcome.NotRunning;
        }

        EngineRecord? rec = EngineRecord.Read(_paths.PidFile);
        if (rec is null || !rec.IsAlive())
        {
            EngineRecord.Delete(_paths.PidFile);   // a stale record, or none
            _log.Write("прокси не был запущен");
            return StopOutcome.NotRunning;
        }

        // The engine and everything it spawned. A native engine is one process, but an older
        // PyInstaller one-file engine (still selectable with TGWS_ENGINE) ran the interpreter as a child;
        // killing only the bootloader would leave it holding the port.
        if (_child is { } own && own.Id == rec.Pid)
            KillTree(own);
        else
        {
            try
            {
                using Process p = Process.GetProcessById(rec.Pid);
                KillTree(p);
            }
            catch (ArgumentException) { /* gone already */ }
        }

        // Not WaitForExit: for a process that is not our child (or a zombie) that can wait forever.
        var clock = Stopwatch.StartNew();
        while (rec.IsAlive())
        {
            if (clock.Elapsed > StopTimeout)
            {
                _log.Write($"не удалось остановить прокси: процесс {rec.Pid} не завершился");
                throw new InvalidOperationException($"процесс {rec.Pid} не завершился за {StopTimeout.TotalSeconds:0} с");
            }
            await Task.Delay(50, ct).ConfigureAwait(false);
        }

        EngineRecord.Delete(_paths.PidFile);
        _log.Write("прокси остановлен");
        return StopOutcome.Stopped;
    }

    public ProxyStatus GetStatus()
    {
        if (_useControlChannel)
        {
            EngineControlResponse? viaWindow = EngineControlClient.TrySend(
                _paths, new EngineControlRequest(EngineControlCommands.Status), TimeSpan.FromMilliseconds(300));
            if (viaWindow is { Ok: true })
                return new ProxyStatus(viaWindow.Running, viaWindow.Listening, viaWindow.Port,
                    viaWindow.PortBusyByOther, viaWindow.Pid);
        }

        ProxySettings s = ProxySettingsStore.Load(IniFile.Load(_paths.Ini));
        EngineRecord? rec = EngineRecord.Read(_paths.PidFile);
        bool running = rec?.IsAlive() == true;
        bool listening = PortProbe.CanConnect(TelegramLink.ConnectAddress(s.Host), s.Port, 300);
        // Busy while we are not running: someone else holds the port.
        bool busyByOther = !running && PortProbe.IsBusy(s.Host, s.Port);
        return new ProxyStatus(running, listening, s.Port, busyByOther, running ? rec!.Pid : null);
    }

    /// <summary>
    /// True when the engine is running with a command line that differs from the one today's settings would give.
    /// That includes the secret: a changed or invalid secret needs a restart or Telegram stops connecting.
    /// </summary>
    public bool RestartNeeded()
    {
        if (_useControlChannel)
        {
            EngineControlResponse? viaWindow = EngineControlClient.TrySend(
                _paths, new EngineControlRequest(EngineControlCommands.RestartNeeded), TimeSpan.FromMilliseconds(300));
            if (viaWindow is { Ok: true }) return viaWindow.RestartNeeded;
        }

        if (EngineRecord.Read(_paths.PidFile) is not { } rec || !rec.IsAlive()) return false;

        ProxySettings s = ProxySettingsStore.Load(IniFile.Load(_paths.Ini));
        if (!ProxySettingsStore.IsValidSecret(s.Secret)) return true;
        return EngineCommand.Hash(EngineCommand.Build(s, _paths)) != rec.ArgvHash;
    }

    private static void KillTree(Process p)
    {
        try { p.Kill(entireProcessTree: true); }
        catch (InvalidOperationException) { /* already exited */ }
        catch (Win32Exception) { /* exiting, or not ours to kill */ }
    }

    // Kill and wait a moment, so "no process left" is true by the time the result is returned.
    private static async Task KillAsync(Process p)
    {
        KillTree(p);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        try { await p.WaitForExitAsync(cts.Token).ConfigureAwait(false); }
        catch (OperationCanceledException) { /* still shutting down; the record is dropped regardless */ }
    }

    // The last lines of what the engine printed to stdout/stderr, for "why did it not start".
    private string ConsoleTail()
    {
        try
        {
            const int window = 64 * 1024;   // an engine that dies at start has little to say; never read a runaway log whole
            using var fs = new FileStream(_paths.EngineConsoleLog, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            bool cut = fs.Length > window;
            if (cut) fs.Seek(-window, SeekOrigin.End);
            using var reader = new StreamReader(fs, Encoding.UTF8);
            if (cut) reader.ReadLine();   // a partial first line

            var tail = new Queue<string>();
            while (reader.ReadLine() is { } line)
            {
                if (tail.Count == OutputTailLines) tail.Dequeue();
                tail.Enqueue(line);
            }
            return string.Join('\n', tail);
        }
        catch (IOException) { return ""; }
        catch (UnauthorizedAccessException) { return ""; }
    }
}
