using TgwsOne.Engine;

namespace TgwsOne.Core;

/// <summary>
/// Maps the settings the application stores onto the engine's immutable configuration. This lives in Core rather than
/// Engine so the engine stays a leaf: it builds for Android without the desktop assembly, and only the host that reads
/// the INI knows how a setting becomes an engine flag.
/// </summary>
public static class EngineConfigFactory
{
    /// <summary>
    /// The settings the application stores, mapped one-to-one to engine flags. <paramref name="logFile"/> is the
    /// proxy log the UI's Журналы page reads.
    /// </summary>
    public static EngineConfig FromSettings(ProxySettings s, string? logFile)
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
}
