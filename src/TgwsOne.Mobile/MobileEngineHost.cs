using System.Globalization;
using TgwsOne.Core;
using TgwsOne.Engine;

namespace TgwsOne.Mobile;

/// <summary>Start, stop, look and "would a restart pick up new settings" — the same shape the desktop host has.</summary>
public interface IProxyEngineControl
{
    Task<EngineStartResult> StartAsync(CancellationToken ct = default);
    Task<EngineStopOutcome> StopAsync(CancellationToken ct = default);
    EngineProxyStatus GetStatus();
    bool RestartNeeded();
}

/// <summary>
/// Hosts the engine inside the app process on Android. The shared settings → config → start → port-switch sequence
/// lives in <see cref="EngineStarter"/>, so this class only owns the lifetime and the answer to "is a restart needed".
/// </summary>
public sealed class MobileEngineHost : IProxyEngineControl, IAsyncDisposable
{
    private readonly MobileStorage _storage;
    private readonly AppLog _log;
    private readonly SemaphoreSlim _gate = new(1, 1);

    private EngineSession? _session;
    private string? _runningHash;
    private bool _disposed;

    public MobileEngineHost(MobileStorage storage, AppLog? log = null)
    {
        _storage = storage;
        _log = log ?? new AppLog(storage.AppLogPath);
    }

    public bool IsRunning => _session is { IsRunning: true };
    public int Port => _session?.Port ?? 0;

    public async Task<EngineStartResult> StartAsync(CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_session is { IsRunning: true })
            {
                _log.Write("прокси уже запущен");
                return new EngineStartResult(EngineStartOutcome.AlreadyRunning, _session.Port);
            }
            if (_session is { } stale)
            {
                // Its listener died outside our control: let go of it before starting a new one.
                _session = null;
                _runningHash = null;
                await stale.DisposeAsync().ConfigureAwait(false);
            }

            IniFile ini = IniFile.Load(_storage.IniPath);
            ProxySettings settings = EngineStarter.LoadSettings(ini, _log);
            EngineStart start = await EngineStarter.StartAsync(ini, settings, _storage.ProxyLogPath, _log, ct)
                .ConfigureAwait(false);
            if (!start.Result.Ok || start.Session is null) return start.Result;

            _session = start.Session;
            _runningHash = Fingerprint(start.Settings, AndroidSettingsStore.Load(ini));
            return start.Result;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<EngineStopOutcome> StopAsync(CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_session is { } session)
            {
                _session = null;
                _runningHash = null;
                await session.DisposeAsync().ConfigureAwait(false);
                _log.Write("прокси остановлен");
                return EngineStopOutcome.Stopped;
            }
            return EngineStopOutcome.NotRunning;
        }
        finally
        {
            _gate.Release();
        }
    }

    public EngineProxyStatus GetStatus()
    {
        if (_session is { IsRunning: true } session)
        {
            ProxySettings settings = ProxySettingsStore.Load(IniFile.Load(_storage.IniPath));
            bool listening = PortProbe.CanConnect(TelegramLink.ConnectAddress(settings.Host), session.Port, 300);
            return new EngineProxyStatus(true, listening, session.Port, PortBusyByOther: false, Environment.ProcessId);
        }
        return new EngineProxyStatus(false, false, 0, PortBusyByOther: false, Pid: null);
    }

    public bool RestartNeeded()
    {
        if (_session is not { IsRunning: true }) return false;

        IniFile ini = IniFile.Load(_storage.IniPath);
        ProxySettings settings = ProxySettingsStore.Load(ini);
        if (!ProxySettingsStore.IsValidSecret(settings.Secret)) return true;
        return Fingerprint(settings, AndroidSettingsStore.Load(ini)) != _runningHash;
    }

    private string Fingerprint(ProxySettings settings, AndroidSettings android)
        => EngineStarter.Hash(settings, _storage.ProxyLogPath)
           + "|" + android.Strategy + "\0" + android.Provider + "\0"
           + android.PoolIdle.ToString(CultureInfo.InvariantCulture);

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;
        try { await StopAsync().ConfigureAwait(false); }
        catch (Exception ex) when (ex is IOException or System.Net.Sockets.SocketException
                                       or InvalidOperationException or TimeoutException) { }
        _gate.Dispose();
    }
}
