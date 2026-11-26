// Record-driver shim for rust-anything (RT4).  `EngineConfigParser.TryParse`
// has an `out` parameter, and the config is a record; this adapter flattens it
// to strings.  A missing `--secret` would make the original generate a random
// one (`NewSecret`, out of scope), so the shim appends a fixed secret to keep
// the recording deterministic; cases that exercise the secret always pass one.
// Not part of TgwsOne.slnx and not compiled into any product assembly.
using System.Globalization;
using TgwsOne.Engine;

namespace TgwsOne.Core;

public static class EngineConfigShims
{
    private const string FixedSecret = "00000000000000000000000000000000";
    private const char Sep = '\u001f';

    // ["help"] / ["err", message] / ["ok", host, port, secret, dcmap, bufferSize,
    //  poolSize, fallback, userDomains, workerDomains, cfH2, h2Enabled,
    //  disableSecure, fakeTlsDomain, proxyProtocol, forceTestDc, verbose,
    //  pingInterval, pingJitter, logFile (\0null or \0value), logMaxMbBits, logBackups].
    public static string[] TryParse(string[] args)
    {
        if (Array.IndexOf(args, "--secret") < 0)
            args = [.. args, "--secret", FixedSecret];

        string? error = EngineConfigParser.TryParse(args, out EngineConfig c);
        if (error == "help") return ["help"];
        if (error != null) return ["err", error];

        string dcMap = string.Join(Sep, c.DcRedirects.OrderBy(pair => pair.Key).Select(pair => $"{pair.Key}:{pair.Value}"));
        return
        [
            "ok",
            c.Host,
            c.Port.ToString(CultureInfo.InvariantCulture),
            c.Secret,
            dcMap,
            c.BufferSize.ToString(CultureInfo.InvariantCulture),
            c.PoolSize.ToString(CultureInfo.InvariantCulture),
            c.FallbackCfProxy ? "1" : "0",
            string.Join(Sep, c.CfproxyUserDomains),
            string.Join(Sep, c.CfproxyWorkerDomains),
            c.CfproxyH2Media ? "1" : "0",
            c.H2Enabled ? "1" : "0",
            c.DisableSecure ? "1" : "0",
            c.FakeTlsDomain,
            c.ProxyProtocol ? "1" : "0",
            c.ForceTestDc ? "1" : "0",
            c.Verbose ? "1" : "0",
            c.PingIntervalSeconds.ToString(CultureInfo.InvariantCulture),
            c.PingJitterSeconds.ToString(CultureInfo.InvariantCulture),
            c.LogFile is null ? "\0null" : "\0" + c.LogFile,
            EncodeDouble(c.LogMaxMb),
            c.LogBackups.ToString(CultureInfo.InvariantCulture),
        ];
    }

    public static string[] CoerceDomains(string[] raw) => [.. EngineConfigParser.CoerceDomains(raw)];

    // `double` bit patterns are not portable for NaN (the sign bit differs);
    // special values get a canonical spelling, the rest keep the exact bits.
    private static string EncodeDouble(double value)
        => double.IsNaN(value) ? "NaN"
            : double.IsPositiveInfinity(value) ? "Infinity"
            : double.IsNegativeInfinity(value) ? "-Infinity"
            : BitConverter.DoubleToInt64Bits(value).ToString(CultureInfo.InvariantCulture);
}
