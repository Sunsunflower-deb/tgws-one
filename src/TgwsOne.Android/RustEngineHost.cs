using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using AndroidLog = Android.Util.Log;
using TgwsOne.Core;
using TgwsOne.Engine;
using TgwsOne.Engine.Desync;
using TgwsOne.Mobile;

namespace TgwsOne.Android;

/// <summary>
/// The one engine instance of the process, backed by the Rust core through <see cref="RustEngine"/>.
///
/// This replaces the former C# <c>EngineRuntime</c>: the foreground service still owns the lifetime, the screens still
/// see the same events and properties, but the listener and every relay session now live in <c>libtgws_core.so</c>.
/// The C# <c>TgwsOne.Engine</c> assembly stays referenced only for UI/shared types (settings, strategies, probes);
/// its <c>ProxyEngine</c> is no longer started from the APK.
/// </summary>
internal static class RustEngineHost
{
    private const string LogTag = "TgwsOne";

    private static readonly SemaphoreSlim Gate = new(1, 1);
    private static RustEngine? _engine;
    private static EngineLog? _engineLog;
    private static DesyncController? _desync;
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
    public static string RustVersion => RustEngine.Version();

    /// <summary>The port the live listener is on, or 0 while there is none.</summary>
    public static int Port => _engine?.Status()?.Port ?? 0;

    /// <summary>Telegram clients attached to the live engine, for the mobile status line and diagnostics.</summary>
    public static long ActiveConnections => _engine?.Status()?.ActiveConnections ?? 0;

    /// <summary>
    /// The UI's bypass controller. RT10a's FFI has no strategy field yet, so this drives the diagnostics/finder pages
    /// only; the Rust relay uses the built-in bypass of the core. When the FFI grows a strategy knob, it is applied
    /// here next to the start config.
    /// </summary>
    public static DesyncController? Desync
    {
        get => _desync;
        set => _desync = value;
    }

    /// <summary>A local probe of the running listener. The FFI has no client-exchange hook, so this only proves the
    /// port is accepting connections; the relay handshake itself is covered by the Rust core's own tests.</summary>
    public static async Task<bool> SelfCheckAsync(TimeSpan timeout, CancellationToken ct)
    {
        int port = Port;
        if (!IsRunning || port <= 0) return false;

        using var limit = CancellationTokenSource.CreateLinkedTokenSource(ct);
        limit.CancelAfter(timeout);
        try
        {
            using var tcp = new TcpClient();
            await tcp.ConnectAsync(IPAddress.Loopback, port, limit.Token).ConfigureAwait(false);
            return true;
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return false;
        }
        catch (Exception ex) when (ex is SocketException or IOException or ObjectDisposedException)
        {
            return false;
        }
    }

    public static async Task StartAsync(string filesDirectory, string? operatorMccMnc = null,
                                        CancellationToken ct = default)
    {
        await Gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_engine is { IsRunning: true }) return;

            _error = null;
            MobileStorage storage = MobileStorage.For(filesDirectory);
            string? logDirectory = Path.GetDirectoryName(storage.ProxyLogPath);
            if (!string.IsNullOrEmpty(logDirectory)) Directory.CreateDirectory(logDirectory);

            var appLog = new AppLog(storage.AppLogPath);
            IniFile ini = IniFile.Load(storage.IniPath);
            ProxySettingsStore.EnsureSecret(ini, appLog);
            ProxySettings settings = ProxySettingsStore.Load(ini);
            AndroidSettings android = AndroidSettingsStore.Load(ini);

            settings = MoveOffBusyPort(ini, settings, appLog, storage.ProxyLogPath);
            var log = new EngineLog(settings.Verbose, storage.ProxyLogPath, settings.LogMaxMb, 1, console: false);
            log.Line += OnEngineLine;

            string? startupError = null;
            var engine = new RustEngine((level, line) =>
            {
                switch (level)
                {
                    case 0: log.Debug(line); break;
                    case 2: log.Warn(line); break;
                    case >= 3:
                        startupError ??= line;
                        log.Error(line);
                        break;
                    default: log.Info(line); break;
                }
            });

            try
            {
                if (!engine.Start(BuildConfig(settings, android)))
                    throw new InvalidOperationException(startupError ?? "Rust-движок не запустился (см. журнал)");
            }
            catch
            {
                log.Line -= OnEngineLine;
                log.Dispose();
                engine.Dispose();
                throw;
            }

            _engineLog = log;
            _appLog = appLog;
            _desync = CreateDesync(android, operatorMccMnc);
            _engine = engine;
            _link = engine.Link();
            _appLog.Write("движок запущен (Rust): " + _link);
            _appLog.Write($"пул: простой {android.PoolIdle} c; оператор {_desync.Operator}");
            AndroidLog.Info(LogTag, "engine started (Rust " + RustVersion + "): " + _link);
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

    public static void OnNetworkChanged(string? operatorMccMnc)
    {
        DesyncController? desync = _desync;
        desync?.SetOperatorFromMccMnc(operatorMccMnc);
        string operatorName = desync?.Operator ?? DesyncRotations.Universal;
        _appLog?.Write("сеть сменилась; оператор " + operatorName);
        AndroidLog.Info(LogTag, "network changed; operator " + operatorName);
        // RT10a's FFI has no live network-change call: the Rust core rebuilds its pools on demand, so there is
        // nothing to poke here yet.
    }

    public static EngineProxyStatus GetStatus()
    {
        bool running = IsRunning;
        int port = Port;
        bool listening = running && port > 0 && PortProbe.CanConnect(IPAddress.Loopback, port, 300);
        return new EngineProxyStatus(running, listening, port, PortBusyByOther: false,
                                     Pid: running ? Environment.ProcessId : null);
    }

    public static bool RestartNeeded() => false;

    private static async Task StopCoreAsync()
    {
        RustEngine? engine = _engine;
        EngineLog? log = _engineLog;
        _engine = null;
        _engineLog = null;
        _desync = null;
        _link = null;

        if (engine is not null)
        {
            _appLog?.Write("останавливаю движок");
            try
            {
                engine.Stop();
                engine.Dispose();
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

    // The stored strategy is either «Авто» or a built-in id; anything else is a typo and falls back to auto.
    private static DesyncController CreateDesync(AndroidSettings android, string? operatorMccMnc)
    {
        var controller = new DesyncController();
        if (!string.IsNullOrEmpty(operatorMccMnc)) controller.SetOperatorFromMccMnc(operatorMccMnc);
        else controller.SetOperator(android.Provider);
        if (android.Strategy == AndroidSettings.DefaultStrategy || BuiltInDesync.Find(android.Strategy) is not { } strategy)
            controller.UseAuto();
        else
            controller.Select(strategy);
        return controller;
    }

    /// <summary>Moves off a busy port as the shared starter does, writing the new port back to the INI.</summary>
    private static ProxySettings MoveOffBusyPort(IniFile ini, ProxySettings settings, AppLog log, string logPath)
    {
        if (!PortProbe.IsBusy(settings.Host, settings.Port)) return settings;

        int first = settings.Port + 1;
        if (PortProbe.FindFree(settings.Host, first, EngineStarter.PortSearchCount) is not { } free)
        {
            log.Write($"порт {settings.Port} занят, свободных портов рядом нет — смените port в настройках");
            throw new InvalidOperationException($"порт {settings.Port} занят, свободного порта рядом нет");
        }

        log.Write($"порт {settings.Port} занят — перехожу на {free}");
        ini.Set("proxy", "port", free.ToString(CultureInfo.InvariantCulture));
        ini.Save();
        return settings with { Port = free };
    }

    /// <summary>The JSON shape of <c>docs/rust-ffi.md</c>. Only the documented fields are sent: the RT10a FFI has no
    /// Cloudflare-domain or strategy knobs, so <c>[proxy] faketls</c> is the domain that reaches the core.</summary>
    private static string BuildConfig(ProxySettings settings, AndroidSettings android)
    {
        var routes = settings.DcMap.ToDictionary(
            entry => entry.Dc.ToString(CultureInfo.InvariantCulture),
            entry => entry.Ip.ToString());

        var config = new Dictionary<string, object?>
        {
            ["host"] = settings.Host.ToString(),
            ["port"] = settings.Port,
            ["secret"] = settings.Secret,
            ["poolidle"] = android.PoolIdle,
            ["routes"] = routes,
            ["pool_size"] = Math.Max(0, settings.PoolSize),
            ["buffer_size"] = Math.Max(4, settings.BufKb) * 1024,
            ["ws_secure"] = !settings.NoSecure,
            ["log_level"] = settings.Verbose ? "verbose" : "normal",
        };
        if (settings.FakeTlsDomain.Length > 0) config["fake_tls_domain"] = settings.FakeTlsDomain;
        return JsonSerializer.Serialize(config);
    }
}
