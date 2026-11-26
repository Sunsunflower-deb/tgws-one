using System.Globalization;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using TgwsOne.Core;
using TgwsOne.Engine.Relay;

namespace TgwsOne.Engine;

/// <summary>How a start ended. Mirrors the desktop host so screens written for it read the same outcomes.</summary>
public enum EngineStartOutcome { Started, AlreadyRunning, PortSwitched, PortExhausted, EngineMissing, EngineExited, Timeout }

/// <param name="Port">The port the engine listens on (or, on failure, the one that was tried).</param>
/// <param name="OldPort">Set after a port switch: the configured port that turned out to be busy.</param>
/// <param name="ExitCode">Set for <see cref="EngineStartOutcome.EngineExited"/> when the engine reported one.</param>
/// <param name="Output">The failure message, for <see cref="EngineStartOutcome.EngineExited"/>.</param>
public sealed record EngineStartResult(EngineStartOutcome Outcome, int Port, int? OldPort = null, int? ExitCode = null, string Output = "")
{
    public bool Ok => Outcome is EngineStartOutcome.Started or EngineStartOutcome.AlreadyRunning or EngineStartOutcome.PortSwitched;
}

public enum EngineStopOutcome { Stopped, NotRunning }

public sealed record EngineProxyStatus(bool Running, bool Listening, int Port, bool PortBusyByOther, int? Pid);

/// <summary>A running engine and the journal it writes to; disposing it stops the engine.</summary>
public sealed class EngineSession : IAsyncDisposable
{
    internal EngineSession(ProxyEngine engine, EngineLog log, EngineConfig config)
    {
        Engine = engine;
        Log = log;
        Config = config;
    }

    public ProxyEngine Engine { get; }
    public EngineLog Log { get; }
    public EngineConfig Config { get; }
    public int Port => Engine.Port;
    public bool IsRunning => Engine.IsRunning;

    public ValueTask DisposeAsync() => Engine.DisposeAsync();
}

/// <summary>The result of <see cref="EngineStarter.StartAsync"/>; <see cref="Session"/> is null when nothing started.</summary>
public sealed record EngineStart(EngineStartResult Result, ProxySettings Settings, EngineSession? Session);

/// <summary>
/// The part of hosting the engine that does not depend on the platform: settings → engine config → start, including
/// moving to the next free port when the configured one is taken.
///
/// The desktop <c>InProcessEngineHost</c> still carries its own copy of this sequence and its process fallback; it
/// migrates to this starter in a separate task, without changing the desktop behaviour. Keeping the shared steps here
/// means the mobile host does not grow a second, drifting definition of what a setting means to the engine.
/// </summary>
public static class EngineStarter
{
    /// <summary>How many ports above the configured one are tried before giving up, as on the desktop.</summary>
    public const int PortSearchCount = 99;

    /// <summary>Makes sure the INI has a secret, then reads the settings the same way the desktop does.</summary>
    public static ProxySettings LoadSettings(IniFile ini, AppLog? log = null)
    {
        ProxySettingsStore.EnsureSecret(ini, log);
        return ProxySettingsStore.Load(ini);
    }

    /// <summary>The same mapping the desktop's <c>EngineConfigFactory</c> applies; Core is not part of the mobile build.</summary>
    public static EngineConfig ToConfig(ProxySettings s, string? logFile)
        => new()
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
            // Masked mode already hides the handshake; an idle pooled socket must not give it away by staying silent.
            PingIntervalSeconds = s.FakeTlsDomain.Length > 0 ? EngineConfig.DefaultPingIntervalSeconds : 0,
            Verbose = s.Verbose,
            LogFile = logFile,
            LogMaxMb = s.LogMaxMb,
            LogBackups = 1,
        };

    /// <summary>
    /// A stable fingerprint of everything that affects the running engine. <c>RestartNeeded</c> compares it with the
    /// one taken at start; the desktop builds the same fingerprint from its command line.
    /// </summary>
    public static string Hash(ProxySettings s, string? logFile)
    {
        var text = new StringBuilder();
        void Add(string? value) => text.Append(value ?? "").Append('\0');

        Add(s.Host.ToString());
        Add(s.Port.ToString(CultureInfo.InvariantCulture));
        Add(s.Secret);
        foreach (DcEntry dc in s.DcMap) Add($"{dc.Dc}:{dc.Ip}");
        Add(s.CfProxy ? "1" : "0");
        Add(s.H2 ? "1" : "0");
        Add(s.NoSecure ? "1" : "0");
        Add(s.Verbose ? "1" : "0");
        foreach (string domain in s.CfDomains) Add(domain);
        Add("|");
        foreach (string domain in s.WorkerDomains) Add(domain);
        Add("|");
        Add(s.BufKb.ToString(CultureInfo.InvariantCulture));
        Add(s.PoolSize.ToString(CultureInfo.InvariantCulture));
        Add(s.LogMaxMb.ToString(CultureInfo.InvariantCulture));
        Add(s.FakeTlsDomain);
        Add(logFile);

        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text.ToString())));
    }

    /// <summary>
    /// Starts an engine from <paramref name="settings"/>. A busy port is not an error: the next free one is written to
    /// the INI and reported as <see cref="EngineStartOutcome.PortSwitched"/> with <see cref="EngineStartResult.OldPort"/> set.
    /// </summary>
    public static async Task<EngineStart> StartAsync(
        IniFile ini, ProxySettings settings, string? proxyLogPath, AppLog? log, CancellationToken ct = default)
    {
        int? oldPort = null;
        if (PortProbe.IsBusy(settings.Host, settings.Port))
        {
            int first = settings.Port + 1;
            if (PortProbe.FindFree(settings.Host, first, PortSearchCount) is not { } free)
            {
                log?.Write($"порт {settings.Port} занят, свободных портов рядом нет — смените port в настройках");
                return new EngineStart(new EngineStartResult(EngineStartOutcome.PortExhausted, settings.Port), settings, null);
            }
            log?.Write($"порт {settings.Port} занят — перехожу на {free}");
            ini.Set("proxy", "port", free.ToString(CultureInfo.InvariantCulture));
            ini.Save();
            oldPort = settings.Port;
            settings = settings with { Port = free };
        }

        EngineConfig config = ToConfig(settings, proxyLogPath);
        var engineLog = new EngineLog(settings.Verbose, proxyLogPath, settings.LogMaxMb, 1, console: false);
        var engine = new ProxyEngine(config, engineLog);
        try
        {
            await engine.StartAsync(ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is SocketException or IOException or InvalidOperationException
                                       or ArgumentException or UnauthorizedAccessException)
        {
            log?.Write("не удалось запустить прокси: " + ex.Message);
            await engine.DisposeAsync().ConfigureAwait(false);
            return new EngineStart(
                new EngineStartResult(EngineStartOutcome.EngineExited, settings.Port, oldPort, Output: ex.Message), settings, null);
        }

        log?.Write($"прокси слушает {settings.Host}:{engine.Port} (движок в процессе приложения)");
        EngineStartResult result = new(
            oldPort is null ? EngineStartOutcome.Started : EngineStartOutcome.PortSwitched, engine.Port, oldPort);
        return new EngineStart(result, settings, new EngineSession(engine, engineLog, config));
    }
}
