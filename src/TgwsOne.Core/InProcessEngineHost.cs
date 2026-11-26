using System.Net;
using System.Net.Sockets;
using TgwsOne.Engine;
using TgwsOne.Engine.Relay;

namespace TgwsOne.Core;

/// <summary>
/// Runs the proxy inside the application process. It keeps the observable behaviour of the old detached engine —
/// the same INI, the same port switching, the same start outcomes — but there is no PyInstaller bootloader, no
/// interpreter child, no GIL and no second process to start, supervise and kill.
///
/// A CLI <c>--start</c> still has to leave something running after it exits: when no window is up it launches this
/// same executable with the engine's command line, which is one native process, not a Python environment.
/// </summary>
public sealed class InProcessEngineHost : IProxyEngineControl, IAsyncDisposable
{
    private readonly AppPaths _paths;
    private readonly AppLog _log;
    private readonly ProxyController _processFallback;
    private readonly TimeSpan _startTimeout;
    private readonly TimeSpan _lockTimeout;
    private readonly SemaphoreSlim _gate = new(1, 1);

    private ProxyEngine? _engine;
    private EngineConfig? _runningConfig;
    private string? _runningHash;
    private bool _disposed;

    public InProcessEngineHost(AppPaths paths, AppLog log, TimeSpan? startTimeout = null, TimeSpan? lockTimeout = null)
    {
        _paths = paths;
        _log = log;
        _startTimeout = startTimeout ?? TimeSpan.FromSeconds(15);
        _lockTimeout = lockTimeout ?? TimeSpan.FromSeconds(20);
        // No control channel: this host *is* what such a channel would reach, and asking itself would recurse.
        _processFallback = new ProxyController(paths, log, _startTimeout, _lockTimeout, useControlChannel: false);
    }

    public bool IsRunningInProcess => _engine is { IsRunning: true };
    public int Port => _engine?.Port ?? 0;

    public async Task<StartResult> StartAsync(CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            return await StartCoreAsync(ct).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<StartResult> StartCoreAsync(CancellationToken ct)
    {
        if (_engine is { IsRunning: true })
        {
            _log.Write("прокси уже запущен");
            return new StartResult(StartOutcome.AlreadyRunning, _engine.Port);
        }
        if (_engine is { } stale)
        {
            // It was stopped outside our control (its listener died): let go of it before starting a new one.
            _engine = null;
            _runningConfig = null;
            _runningHash = null;
            await stale.DisposeAsync().ConfigureAwait(false);
        }

        await using FileStream startLock = await AcquireStartLockAsync(ct).ConfigureAwait(false);

        // A detached engine started by `tgwsone --start` before the window opened is just as good: adopt it by
        // reporting AlreadyRunning, and let Stop/Status go to the process controller.
        ProxyStatus detached = _processFallback.GetStatus();
        if (detached.Running)
        {
            _log.Write("прокси уже запущен (отдельным процессом)");
            return new StartResult(StartOutcome.AlreadyRunning, detached.Port);
        }

        IniFile ini = IniFile.Load(_paths.Ini);
        ProxySettingsStore.EnsureSecret(ini, _log);
        ProxySettings s = ProxySettingsStore.Load(ini);

        // TGWS_ENGINE is a test and diagnostics switch (a missing one used to mean EngineMissing); the built-in
        // engine itself needs no file, but honouring the switch keeps the "engine not found" scene meaningful.
        if (_paths.EngineOverridden && !File.Exists(_paths.Engine))
        {
            _log.Write("движок не найден: " + _paths.Engine);
            return new StartResult(StartOutcome.EngineMissing, s.Port);
        }

        int? oldPort = null;
        if (PortProbe.IsBusy(s.Host, s.Port))
        {
            int first = s.Port + 1;
            if (PortProbe.FindFree(s.Host, first, 99) is not { } free)
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

        EngineConfig config = EngineConfigFactory.FromSettings(s, _paths.ProxyLogFile);
        var engine = new ProxyEngine(config, new EngineLog(s.Verbose, _paths.ProxyLogFile, s.LogMaxMb, 1, console: false));
        try
        {
            await engine.StartAsync(ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is SocketException or IOException or InvalidOperationException
                                       or ArgumentException or UnauthorizedAccessException)
        {
            _log.Write("не удалось запустить прокси: " + ex.Message);
            await engine.DisposeAsync().ConfigureAwait(false);
            return new StartResult(StartOutcome.EngineExited, s.Port, oldPort, Output: ex.Message);
        }

        _engine = engine;
        _runningConfig = config;
        _runningHash = EngineCommand.Hash(EngineCommand.Build(s, _paths));
        _log.Write($"прокси слушает {s.Host}:{engine.Port} (движок в процессе приложения)");
        return new StartResult(oldPort is null ? StartOutcome.Started : StartOutcome.PortSwitched,
            engine.Port, oldPort);
    }

    private async Task<FileStream> AcquireStartLockAsync(CancellationToken ct)
    {
        PrivateDirectory.Ensure(_paths.RunDir);
        var clock = System.Diagnostics.Stopwatch.StartNew();
        while (true)
        {
            try
            {
                return new FileStream(_paths.StartLockFile, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None,
                    bufferSize: 1);
            }
            catch (IOException) when (clock.Elapsed < _lockTimeout)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(100), ct).ConfigureAwait(false);
            }
            catch (IOException ex)
            {
                _log.Write("другой запуск прокси ещё не завершился");
                throw new TimeoutException($"другой запуск прокси не завершился за {_lockTimeout.TotalSeconds:0} с", ex);
            }
        }
    }

    public async Task<StopOutcome> StopAsync(CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_engine is { } engine)
            {
                _engine = null;
                _runningConfig = null;
                _runningHash = null;
                await engine.DisposeAsync().ConfigureAwait(false);
                _log.Write("прокси остановлен");
                return StopOutcome.Stopped;
            }
            return await _processFallback.StopAsync(ct).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    public ProxyStatus GetStatus()
    {
        if (_engine is { IsRunning: true } engine)
        {
            ProxySettings s = ProxySettingsStore.Load(IniFile.Load(_paths.Ini));
            bool listening = PortProbe.CanConnect(TelegramLink.ConnectAddress(s.Host), engine.Port, 300);
            return new ProxyStatus(true, listening, engine.Port, PortBusyByOther: false, Environment.ProcessId);
        }
        return _processFallback.GetStatus();
    }

    public bool RestartNeeded()
    {
        ProxyEngine? engine = _engine;
        if (engine is null || !engine.IsRunning) return _processFallback.RestartNeeded();

        ProxySettings s = ProxySettingsStore.Load(IniFile.Load(_paths.Ini));
        if (!ProxySettingsStore.IsValidSecret(s.Secret)) return true;
        return EngineCommand.Hash(EngineCommand.Build(s, _paths)) != _runningHash;
    }

    /// <summary>The window's control channel delegates here: <c>tgwsone --start/--stop/--status</c> then act on this process.</summary>
    public async Task<EngineControlResponse> HandleControlAsync(EngineControlRequest request, CancellationToken ct = default)
    {
        try
        {
            switch (request.Cmd)
            {
                case EngineControlCommands.Start:
                {
                    StartResult r = await StartAsync(ct).ConfigureAwait(false);
                    return new EngineControlResponse
                    {
                        Ok = r.Ok,
                        Outcome = r.Outcome.ToString(),
                        Port = r.Port,
                        OldPort = r.OldPort,
                        ExitCode = r.ExitCode,
                        Output = r.Output,
                    };
                }
                case EngineControlCommands.Stop:
                {
                    StopOutcome o = await StopAsync(ct).ConfigureAwait(false);
                    return new EngineControlResponse { Ok = true, Outcome = o.ToString() };
                }
                case EngineControlCommands.Status:
                {
                    ProxyStatus st = GetStatus();
                    return new EngineControlResponse
                    {
                        Ok = true,
                        Running = st.Running,
                        Listening = st.Listening,
                        Port = st.Port,
                        PortBusyByOther = st.PortBusyByOther,
                        Pid = st.Pid,
                    };
                }
                case EngineControlCommands.RestartNeeded:
                    return new EngineControlResponse { Ok = true, RestartNeeded = RestartNeeded() };
                default:
                    return EngineControlResponse.Failure($"неизвестная команда: {request.Cmd}");
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return EngineControlResponse.Failure(ex.Message);
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;
        try { await StopAsync().ConfigureAwait(false); }
        catch (Exception ex) when (ex is IOException or SocketException or InvalidOperationException or TimeoutException) { }
        _gate.Dispose();
    }
}
