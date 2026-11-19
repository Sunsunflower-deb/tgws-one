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
public sealed class ProxyController
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(250);
    private static readonly TimeSpan StopTimeout = TimeSpan.FromSeconds(5);
    private const int OutputTailLines = 20;

    private readonly AppPaths _paths;
    private readonly AppLog _log;
    private readonly TimeSpan _startTimeout;

    // The engine this instance started last. Holding the Process lets .NET reap it when it exits,
    // so a long-lived host (the GUI) does not collect a zombie per restart.
    private Process? _child;

    public ProxyController(AppPaths paths, AppLog log, TimeSpan? startTimeout = null)
    {
        _paths = paths;
        _log = log;
        _startTimeout = startTimeout ?? TimeSpan.FromSeconds(15);
    }

    public async Task<StartResult> StartAsync(CancellationToken ct = default)
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

        DateTime startedUtc;
        try { startedUtc = p.StartTime.ToUniversalTime(); }
        catch (InvalidOperationException) { startedUtc = DateTime.UtcNow; }   // it exited before we could ask
        new EngineRecord(p.Id, startedUtc, _paths.Engine, EngineCommand.Hash(argv)).Write(_paths.PidFile);
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
        EngineRecord? rec = EngineRecord.Read(_paths.PidFile);
        if (rec is null || !rec.IsAlive())
        {
            EngineRecord.Delete(_paths.PidFile);   // a stale record, or none
            _log.Write("прокси не был запущен");
            return StopOutcome.NotRunning;
        }

        // The engine and everything it spawned: the PyInstaller one-file bootloader runs the real
        // interpreter as a child, and killing only the bootloader would leave it holding the port.
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
