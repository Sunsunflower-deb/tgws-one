using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Net;
using System.Security.Cryptography;

namespace TgwsOne.Core;

public sealed record DcEntry(int Dc, IPAddress Ip);

/// <summary>Validated proxy settings: what the engine is started with.</summary>
public sealed record ProxySettings(
    IPAddress Host, int Port, IReadOnlyList<DcEntry> DcMap,
    bool CfProxy, bool H2, bool NoSecure, bool Verbose,
    IReadOnlyList<string> CfDomains, IReadOnlyList<string> WorkerDomains,
    int BufKb, int PoolSize, int LogMaxMb, string Secret,
    string FakeTlsDomain = "");

/// <summary>Raw values as a settings window holds them (SP3 binds to this).</summary>
public sealed record ProxyForm(
    string Host, string Port, string DcMap, bool CfProxy, bool H2, bool NoSecure, bool Verbose,
    string CfDomains, string WorkerDomains, string BufKb, string PoolSize, string LogMaxMb,
    string FakeTlsDomain = "");

/// <summary>A validation problem; <see cref="Field"/> is the <see cref="ProxyForm"/> property name.</summary>
public sealed record FieldError(string Field, string Message);

/// <summary>Reads, validates and writes the <c>[proxy]</c> section and the secret in <c>[app]</c>.</summary>
public static class ProxySettingsStore
{
    private const string DefaultHost = "127.0.0.1";
    private const int DefaultPort = 1443;
    private const string DefaultDcMap = "2:149.154.167.220,4:149.154.167.220";
    private const int DefaultBufKb = 256;
    private const int DefaultPoolSize = 4;
    private const int DefaultLogMaxMb = 5;

    /// <summary>INI strings with 1.x defaults where absent. The DC map is one entry per line.</summary>
    public static ProxyForm ReadForm(IniFile ini)
    {
        // 1.x GetStr: an empty value counts as absent (except where noted below).
        string Str(string key, string def) => ini.Get("proxy", key) is { Length: > 0 } v ? v : def;
        string Num(string key, int def) =>
            (int.TryParse(Str(key, ""), NumberStyles.Integer, CultureInfo.InvariantCulture, out int n) ? n : def)
            .ToString(CultureInfo.InvariantCulture);
        bool Flag(string key, bool def) => ParseBool(ini.Get("proxy", key), def);

        return new ProxyForm(
            Host: Str("host", DefaultHost),
            Port: Num("port", DefaultPort),
            // 1.x GetRaw: an empty DC map is a real value (the user cleared the list), not "absent".
            DcMap: (ini.Get("proxy", "dcmap") ?? DefaultDcMap).Replace(",", "\n"),
            CfProxy: Flag("cfproxy", true),
            H2: Flag("h2", true),
            NoSecure: Flag("nosecure", false),
            Verbose: Flag("verbose", true),
            CfDomains: Str("cfdomains", ""),
            WorkerDomains: Str("workerdomains", ""),
            BufKb: Num("bufkb", DefaultBufKb),
            PoolSize: Num("poolsize", DefaultPoolSize),
            LogMaxMb: Num("logmaxmb", DefaultLogMaxMb),
            // Empty means "no fake TLS": the link stays a dd-secret and nothing is masked.
            FakeTlsDomain: Str("faketls", ""));
    }

    /// <summary>Validates a form. All problems are reported, in field order; settings are null if there are any.</summary>
    public static (ProxySettings? Settings, IReadOnlyList<FieldError> Errors) Parse(ProxyForm form, string secret)
    {
        var errors = new List<FieldError>();

        if (!TryPort(form.Port, out int port))
            errors.Add(new FieldError(nameof(ProxyForm.Port), "Порт должен быть числом от 1 до 65535."));
        if (!TryHost(form.Host, out IPAddress? host))
            errors.Add(new FieldError(nameof(ProxyForm.Host), "Хост должен быть IP-адресом, например 127.0.0.1."));
        List<DcEntry>? dcMap = ParseDcMap(form.DcMap, out FieldError? dcError);
        if (dcError != null) errors.Add(dcError);
        if (!TryInt(form.BufKb, 4, out int bufKb))
            errors.Add(new FieldError(nameof(ProxyForm.BufKb), "Буфер — не меньше 4 КБ."));
        if (!TryInt(form.PoolSize, 0, out int poolSize))
            errors.Add(new FieldError(nameof(ProxyForm.PoolSize), "Пул WS — не меньше 0."));
        if (!TryInt(form.LogMaxMb, 1, out int logMaxMb))
            errors.Add(new FieldError(nameof(ProxyForm.LogMaxMb), "Размер лога — не меньше 1 МБ."));
        bool hasDomain = form.FakeTlsDomain.Trim().Length > 0;
        if (hasDomain && !TryDomain(form.FakeTlsDomain, out string fakeTlsDomain))
            errors.Add(new FieldError(nameof(ProxyForm.FakeTlsDomain),
                "Домен для маскировки — имя вида www.example.com, без схемы и пути."));

        if (errors.Count > 0) return (null, errors);
        return (new ProxySettings(host!, port, dcMap!, form.CfProxy, form.H2, form.NoSecure, form.Verbose,
            SplitList(form.CfDomains), SplitList(form.WorkerDomains), bufKb, poolSize, logMaxMb, secret,
            hasDomain ? NormalizeDomain(form.FakeTlsDomain) : ""), errors);
    }

    /// <summary>
    /// Settings from the INI; each field that is missing or invalid falls back to its default. Never throws.
    /// The secret is the stored one if valid, otherwise empty: <see cref="EnsureSecret"/> creates it.
    /// </summary>
    public static ProxySettings Load(IniFile ini)
    {
        ProxyForm f = ReadForm(ini);
        List<DcEntry> dcMap = ParseDcMap(f.DcMap, out _)
            ?? ParseDcMap(DefaultDcMap, out _)!;
        string? secret = ini.Get("app", "secret");

        return new ProxySettings(
            Host: TryHost(f.Host, out IPAddress? host) ? host : IPAddress.Parse(DefaultHost),
            Port: TryPort(f.Port, out int port) ? port : DefaultPort,
            DcMap: dcMap,
            CfProxy: f.CfProxy, H2: f.H2, NoSecure: f.NoSecure, Verbose: f.Verbose,
            CfDomains: SplitList(f.CfDomains),
            WorkerDomains: SplitList(f.WorkerDomains),
            BufKb: TryInt(f.BufKb, 4, out int bufKb) ? bufKb : DefaultBufKb,
            PoolSize: TryInt(f.PoolSize, 0, out int poolSize) ? poolSize : DefaultPoolSize,
            LogMaxMb: TryInt(f.LogMaxMb, 1, out int logMaxMb) ? logMaxMb : DefaultLogMaxMb,
            Secret: IsValidSecret(secret) ? secret : "",
            FakeTlsDomain: TryDomain(f.FakeTlsDomain, out string fakeTls) ? NormalizeDomain(fakeTls) : "");
    }

    /// <summary>Writes the <c>[proxy]</c> keys and <c>[app] secret</c>, then saves the file.</summary>
    public static void Save(IniFile ini, ProxySettings s)
    {
        string Flag(bool b) => b ? "1" : "0";
        ini.Set("proxy", "host", s.Host.ToString());
        ini.Set("proxy", "port", s.Port.ToString(CultureInfo.InvariantCulture));
        ini.Set("proxy", "dcmap", string.Join(",", s.DcMap.Select(d => $"{d.Dc}:{d.Ip}")));
        ini.Set("proxy", "cfproxy", Flag(s.CfProxy));
        ini.Set("proxy", "h2", Flag(s.H2));
        ini.Set("proxy", "cfdomains", string.Join(",", s.CfDomains));
        ini.Set("proxy", "workerdomains", string.Join(",", s.WorkerDomains));
        ini.Set("proxy", "nosecure", Flag(s.NoSecure));
        ini.Set("proxy", "verbose", Flag(s.Verbose));
        ini.Set("proxy", "bufkb", s.BufKb.ToString(CultureInfo.InvariantCulture));
        ini.Set("proxy", "poolsize", s.PoolSize.ToString(CultureInfo.InvariantCulture));
        ini.Set("proxy", "logmaxmb", s.LogMaxMb.ToString(CultureInfo.InvariantCulture));
        ini.Set("proxy", "faketls", s.FakeTlsDomain);
        ini.Set("app", "secret", s.Secret);
        ini.Save();
    }

    /// <summary>Returns the stored secret, or creates, saves and returns a new one if it is missing or invalid.</summary>
    public static string EnsureSecret(IniFile ini, AppLog? log = null)
    {
        string? current = ini.Get("app", "secret");
        if (IsValidSecret(current)) return current;

        string secret = NewSecret();
        ini.Set("app", "secret", secret);
        ini.Save();
        log?.Write("создан новый secret (сохранён в настройках)");
        return secret;
    }

    /// <summary>32 lower-case hex characters (16 random bytes).</summary>
    public static string NewSecret() => Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(16));

    /// <summary>Exactly 32 hex characters; the engine refuses anything else.</summary>
    public static bool IsValidSecret([NotNullWhen(true)] string? s)
        => s is { Length: 32 } && s.All(Uri.IsHexDigit);

    /// <summary>Splits on <c>, ; space tab</c> and line breaks, trims, drops empties and duplicates (ordinal).</summary>
    public static IReadOnlyList<string> SplitList(string? raw)
        => raw is null
            ? []
            : raw.Split([',', ';', ' ', '\t', '\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Distinct(StringComparer.Ordinal).ToList();

    // ---------- field parsers shared by Parse and Load ----------

    // 1.x: "1", "true", "yes" (any case) are true; empty means "use the default"; anything else is false.
    private static bool ParseBool(string? v, bool def)
    {
        if (string.IsNullOrEmpty(v)) return def;
        return v == "1" || v.Equals("true", StringComparison.OrdinalIgnoreCase) || v.Equals("yes", StringComparison.OrdinalIgnoreCase);
    }

    private static bool TryHost(string s, [NotNullWhen(true)] out IPAddress? ip) => IPAddress.TryParse(s.Trim(), out ip);

    // The masking domain goes into a tg:// link and into the engine's --fake-tls-domain: a bare host name only.
    private static bool TryDomain(string raw, out string domain)
    {
        domain = raw.Trim().TrimEnd('.').ToLowerInvariant();
        if (domain.Length is 0 or > 253) return false;
        string[] labels = domain.Split('.');
        if (labels.Length < 2) return false;
        foreach (string label in labels)
        {
            if (label.Length is 0 or > 63) return false;
            if (label[0] == '-' || label[^1] == '-') return false;
            if (!label.All(c => char.IsAsciiLetterOrDigit(c) || c == '-')) return false;
        }
        return labels[^1].Any(char.IsAsciiLetter);       // a top-level domain is never all digits
    }

    private static string NormalizeDomain(string raw) => raw.Trim().TrimEnd('.').ToLowerInvariant();

    private static bool TryPort(string s, out int port)
        => int.TryParse(s.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out port) && port is >= 1 and <= 65535;

    private static bool TryInt(string s, int min, out int value)
        => int.TryParse(s.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out value) && value >= min;

    // Accepts lines and/or commas, one "N:IP" each. An empty result is valid (the user cleared the list).
    private static List<DcEntry>? ParseDcMap(string raw, out FieldError? error)
    {
        error = null;
        var map = new List<DcEntry>();
        foreach (string line in raw.Split([',', '\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            int colon = line.IndexOf(':');
            if (colon <= 0)
            {
                error = new FieldError(nameof(ProxyForm.DcMap),
                    $"Строка DC → IP должна быть в формате номер:IP, например 4:149.154.167.220. Проблемная строка: {line}");
                return null;
            }
            if (!int.TryParse(line[..colon].Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int dc)
                || !IPAddress.TryParse(line[(colon + 1)..].Trim(), out IPAddress? ip))
            {
                error = new FieldError(nameof(ProxyForm.DcMap), $"Не удалось разобрать строку DC → IP: {line}");
                return null;
            }
            map.Add(new DcEntry(dc, ip));
        }
        return map;
    }
}
