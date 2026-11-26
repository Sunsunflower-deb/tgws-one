using AndroidLog = Android.Util.Log;
using TgwsOne.Core;
using TgwsOne.Engine;
using TgwsOne.Engine.Relay;
using TgwsOne.Mobile;

namespace TgwsOne.Android;

/// <summary>
/// The one engine instance of the process. The foreground service owns its lifetime; the debug screen only starts
/// and stops the service and watches this state, so there is never a second listener on the same port.
/// </summary>
internal static class EngineRuntime
{
    private const string LogTag = "TgwsOne";

    private static readonly SemaphoreSlim Gate = new(1, 1);
    private static ProxyEngine? _engine;
    private static EngineLog? _engineLog;
    private static AppLog? _appLog;
    private static string? _link;
    private static string? _error;

    /// <summary>Raised after every state change (started, stopped, failed). May run on a background thread.</summary>
    public static event Action? Changed;

    /// <summary>Engine journal lines, also written to logcat under <c>TgwsOne</c>. May run on a background thread.</summary>
    public static event Action<string>? Line;

    public static bool IsRunning => _engine is { IsRunning: true };
    public static string? Link => _link;
    public static string? Error => _error;

    public static async Task StartAsync(string filesDirectory, CancellationToken ct = default)
    {
        await Gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_engine is { IsRunning: true }) return;

            _error = null;
            MobileStorage storage = MobileStorage.For(filesDirectory);
            string? logDirectory = Path.GetDirectoryName(storage.ProxyLogPath);
            if (!string.IsNullOrEmpty(logDirectory)) Directory.CreateDirectory(logDirectory);

            _appLog = new AppLog(storage.AppLogPath);
            IniFile ini = IniFile.Load(storage.IniPath);
            ProxySettingsStore.EnsureSecret(ini, _appLog);
            ProxySettings settings = ProxySettingsStore.Load(ini);

            EngineConfig config = ToEngineConfig(settings, storage.ProxyLogPath);
            var log = new EngineLog(settings.Verbose, storage.ProxyLogPath, settings.LogMaxMb, 1, console: false);
            log.Line += OnEngineLine;
            var engine = new ProxyEngine(config, log);
            try
            {
                await engine.StartAsync(ct).ConfigureAwait(false);
            }
            catch
            {
                log.Line -= OnEngineLine;
                log.Dispose();
                await engine.DisposeAsync().ConfigureAwait(false);
                throw;
            }

            _engineLog = log;
            _engine = engine;
            _link = engine.TelegramLink();
            _appLog.Write("движок запущен: " + _link);
            AndroidLog.Info(LogTag, "engine started: " + _link);
            Changed?.Invoke();
        }
        catch (Exception ex)
        {
            _error = ex.Message;
            _appLog?.Write("не удалось запустить движок: " + ex.Message);
            AndroidLog.Error(LogTag, "engine failed to start: " + ex);
            Changed?.Invoke();
            throw;
        }
        finally
        {
            Gate.Release();
        }
    }

    public static async Task StopAsync()
    {
        await Gate.WaitAsync().ConfigureAwait(false);
        try
        {
            await StopCoreAsync().ConfigureAwait(false);
        }
        finally
        {
            Gate.Release();
        }
    }

    /// <summary>For <c>Service.OnDestroy</c>, which cannot await: the teardown is short and must finish before the process may die.</summary>
    public static void StopBlocking() => StopAsync().GetAwaiter().GetResult();

    private static async Task StopCoreAsync()
    {
        ProxyEngine? engine = _engine;
        EngineLog? log = _engineLog;
        _engine = null;
        _engineLog = null;
        _link = null;

        if (engine is not null)
        {
            _appLog?.Write("останавливаю движок");
            try
            {
                await engine.DisposeAsync().ConfigureAwait(false);
                AndroidLog.Info(LogTag, "engine stopped");
            }
            catch (Exception ex)
            {
                AndroidLog.Warn(LogTag, "engine stop failed: " + ex.Message);
            }
        }
        if (log is not null)
        {
            log.Line -= OnEngineLine;
            log.Dispose();
        }
        Changed?.Invoke();
    }

    private static void OnEngineLine(string line)
    {
        AndroidLog.Info(LogTag, line);
        try { Line?.Invoke(line); } catch { /* a faulty subscriber must not break the engine */ }
    }

    // The same mapping EngineConfigFactory applies on the desktop; Core is not available to an Android build.
    private static EngineConfig ToEngineConfig(ProxySettings s, string logFile) => new()
    {
        Host = s.Host.ToString(),
        Port = s.Port,
        Secret = s.Secret,
        DcRedirects = s.DcMap.ToDictionary(d => d.Dc, d => d.Ip.ToString()),
        BufferSize = Math.Max(4, s.BufKb) * 1024,
        PoolSize = Math.Max(0, s.PoolSize),
        FallbackCfProxy = s.CfProxy,
        CfproxyUserDomains = s.CfDomains,
        CfproxyWorkerDomains = s.WorkerDomains,
        CfproxyH2Media = s.H2,
        DisableSecure = s.NoSecure,
        FakeTlsDomain = s.FakeTlsDomain,
        PingIntervalSeconds = s.FakeTlsDomain.Length > 0 ? EngineConfig.DefaultPingIntervalSeconds : 0,
        Verbose = s.Verbose,
        LogFile = logFile,
        LogMaxMb = s.LogMaxMb,
        LogBackups = 1,
    };
}
