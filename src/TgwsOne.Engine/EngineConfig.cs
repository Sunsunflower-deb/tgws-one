using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using System.Text;

namespace TgwsOne.Engine;

/// <summary>
/// Everything the engine needs, the same values the Python tg-ws-proxy v1.11.4 command line carried. Immutable: a
/// restart with different settings is a new engine instance.
/// </summary>
public sealed record EngineConfig
{
    public const int DefaultPort = 1443;
    public const string DefaultHost = "127.0.0.1";

    public string Host { get; init; } = DefaultHost;
    public int Port { get; init; } = DefaultPort;
    public string Secret { get; init; } = "";
    public IReadOnlyDictionary<int, string> DcRedirects { get; init; } = new Dictionary<int, string>
    {
        [2] = "149.154.167.220",
        [4] = "149.154.167.220",
    };

    public int BufferSize { get; init; } = 256 * 1024;
    public int PoolSize { get; init; } = 4;
    public bool FallbackCfProxy { get; init; } = true;
    public IReadOnlyList<string> CfproxyUserDomains { get; init; } = [];
    public IReadOnlyList<string> CfproxyWorkerDomains { get; init; } = [];
    public bool CfproxyH2Media { get; init; } = true;
    public bool DisableSecure { get; init; }
    public string FakeTlsDomain { get; init; } = "";
    public bool ProxyProtocol { get; init; }
    public bool ForceTestDc { get; init; }
    public bool Verbose { get; init; }

    /// <summary>How often a pooled WebSocket gets a keep-alive ping; 0 turns the pings off.</summary>
    public int PingIntervalSeconds { get; init; }

    /// <summary>Random extra delay added to every ping, so the rhythm is not one constant on the wire.</summary>
    public int PingJitterSeconds { get; init; } = DefaultPingJitterSeconds;

    /// <summary>Masked mode keeps its connections warm: an idle socket that says nothing for minutes is a pattern too.</summary>
    public const int DefaultPingIntervalSeconds = 25;
    public const int DefaultPingJitterSeconds = 10;

    public string? LogFile { get; init; }
    public double LogMaxMb { get; init; } = 5;
    public int LogBackups { get; init; } = 1;

    /// <summary>Where the direct TCP fallback connects. 443 in production; a test rig points it at a local server.</summary>
    internal int TcpFallbackPort { get; init; } = 443;

    /// <summary>Overrides the DC address the TCP fallback dials (a local fake Telegram in tests).</summary>
    internal string? TcpFallbackHostOverride { get; init; }

    /// <summary>HTTP/2 for media through Cloudflare: only where the plain CF-proxy over HTTPS is usable at all.</summary>
    public bool H2Enabled => CfproxyH2Media && FallbackCfProxy && !DisableSecure && !ForceTestDc;

    public static string NewSecret() => Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(16));

    public static bool IsValidSecret(string? s) => s is { Length: 32 } && s.All(Uri.IsHexDigit);

    /// <summary>
    /// The <c>tg://proxy</c> link a client adds; wildcard listen addresses become loopback (or the LAN address).
    /// With a masking domain the link carries an <c>ee</c>-secret followed by that domain in hex: such a client speaks
    /// the fake-TLS handshake and looks like a visit to that site, instead of the bare <c>dd</c> obfuscated2 one.
    /// </summary>
    public string TelegramLink()
    {
        string linkHost = GetLinkHost(Host);
        string secret = FakeTlsDomain.Length > 0
            ? "ee" + Secret + Convert.ToHexStringLower(Encoding.ASCII.GetBytes(FakeTlsDomain))
            : "dd" + Secret;
        return $"tg://proxy?server={Uri.EscapeDataString(linkHost)}&port={Port}&secret={secret}";
    }

    // Python's get_link_host: 0.0.0.0 is replaced by the address the machine would use to reach the internet.
    internal static string GetLinkHost(string host)
    {
        if (host != "0.0.0.0") return host;
        try
        {
            using var s = new System.Net.Sockets.Socket(
                System.Net.Sockets.AddressFamily.InterNetwork, System.Net.Sockets.SocketType.Dgram,
                System.Net.Sockets.ProtocolType.Udp);
            s.Connect("8.8.8.8", 80);
            return ((IPEndPoint)s.LocalEndPoint!).Address.ToString();
        }
        catch (Exception ex) when (ex is System.Net.Sockets.SocketException or InvalidOperationException)
        {
            return "127.0.0.1";
        }
    }
}

/// <summary>
/// The engine's command line: exactly the flags tg-ws-proxy v1.11.4 accepted, because the
/// <c>EngineCommand</c> in Core still builds them and existing installations, scripts and the
/// process fallback pass them through.
/// </summary>
public static class EngineConfigParser
{
    public const string Usage = """
        Использование: tgwsone --engine [ключи]

          --port N                    порт прослушивания (по умолчанию 1443)
          --host ADDR                 адрес прослушивания (по умолчанию 127.0.0.1)
          --secret HEX                32 шестнадцатеричных символа; без ключа генерируется
          --dc-ip [DC:IP]             адрес дата-центра (можно повторять); без значения — пустой список
          -v, --verbose               подробный журнал
          --log-file PATH             журнал в файл с ротацией
          --log-max-mb MB             предел файла журнала (по умолчанию 5)
          --log-backups N             сколько старых файлов хранить (по умолчанию 1)
          --buf-kb KB                 размер буфера сокета в КБ (по умолчанию 256)
          --pool-size N               размер пула WS на DC (по умолчанию 4, 0 — без прямого WS)
          --cfproxy-domain DOMAIN     свой домен Cloudflare (можно повторять)
          --cfproxy-worker-domain D   домен Cloudflare Worker (можно повторять)
          --no-h2                     выключить HTTP/2 для медиа через Cloudflare
          --no-cfproxy                выключить запасной путь через Cloudflare
          --no-secure                 порт 80 для Cloudflare-соединений
          --fake-tls-domain DOMAIN    замаскировать прокси под TLS с этим доменом
          --ping-interval N           пинг простаивающего пула WS, секунды (0 — выключено)
          --ping-jitter N             случайная добавка к интервалу пинга, секунды (по умолчанию 10)
          --force-test-dc             весь трафик — в тестовые дата-центры Telegram
          --proxy-protocol            принимать заголовок PROXY protocol v1
          -h, --help                  эта справка
        """;

    /// <summary>Null when the arguments are valid; otherwise a message for stderr.</summary>
    public static string? TryParse(string[] args, out EngineConfig config)
    {
        config = new EngineConfig();

        string host = EngineConfig.DefaultHost;
        int port = EngineConfig.DefaultPort;
        string? secret = null;
        var dcIp = new List<string?>();
        bool dcIpGiven = false;
        bool verbose = false;
        string? logFile = null;
        double logMaxMb = 5;
        int logBackups = 1;
        int bufKb = 256;
        int poolSize = 4;
        var cfDomains = new List<string>();
        var workerDomains = new List<string>();
        bool noH2 = false, noCfproxy = false, noSecure = false, forceTestDc = false, proxyProtocol = false;
        string fakeTlsDomain = "";
        int pingInterval = 0;
        int pingJitter = EngineConfig.DefaultPingJitterSeconds;

        for (int i = 0; i < args.Length; i++)
        {
            string a = args[i];
            string? Next() => i + 1 < args.Length ? args[++i] : null;

            switch (a)
            {
                case "-h" or "--help":
                    return "help";
                case "--host":
                {
                    string? v = Next();
                    if (v is null) return "после --host нужен адрес";
                    // The listener is bound to an address, not a name: caught here, and not as a library
                    // message when the engine is already starting.
                    if (!IPAddress.TryParse(v, out _)) return $"--host ждёт IP-адрес, например 127.0.0.1 или 0.0.0.0 (получилось «{v}»)";
                    host = v;
                    break;
                }
                case "--port":
                {
                    string? v = Next();
                    if (v is null || !int.TryParse(v, NumberStyles.Integer, CultureInfo.InvariantCulture, out port))
                        return "после --port нужно число";
                    if (port is < 1 or > 65535) return $"--port должен быть от 1 до 65535 (получилось {port})";
                    break;
                }
                case "--secret":
                {
                    secret = Next();
                    if (secret is null) return "после --secret нужен секрет";
                    break;
                }
                case "--dc-ip":
                {
                    dcIpGiven = true;
                    string? v = i + 1 < args.Length && !args[i + 1].StartsWith('-') ? args[++i] : null;
                    dcIp.Add(v);
                    break;
                }
                case "-v" or "--verbose":
                    verbose = true;
                    break;
                case "--log-file":
                {
                    logFile = Next();
                    if (logFile is null) return "после --log-file нужен путь";
                    break;
                }
                case "--log-max-mb":
                {
                    string? v = Next();
                    if (v is null || !double.TryParse(v, NumberStyles.Float, CultureInfo.InvariantCulture, out logMaxMb))
                        return "после --log-max-mb нужно число";
                    break;
                }
                case "--log-backups":
                {
                    string? v = Next();
                    if (v is null || !int.TryParse(v, NumberStyles.Integer, CultureInfo.InvariantCulture, out logBackups))
                        return "после --log-backups нужно число";
                    break;
                }
                case "--buf-kb":
                {
                    string? v = Next();
                    if (v is null || !int.TryParse(v, NumberStyles.Integer, CultureInfo.InvariantCulture, out bufKb))
                        return "после --buf-kb нужно число";
                    break;
                }
                case "--pool-size":
                {
                    string? v = Next();
                    if (v is null || !int.TryParse(v, NumberStyles.Integer, CultureInfo.InvariantCulture, out poolSize))
                        return "после --pool-size нужно число";
                    break;
                }
                case "--cfproxy-domain":
                {
                    string? v = Next();
                    if (v is null) return "после --cfproxy-domain нужен домен";
                    cfDomains.Add(v);
                    break;
                }
                case "--cfproxy-worker-domain":
                {
                    string? v = Next();
                    if (v is null) return "после --cfproxy-worker-domain нужен домен";
                    workerDomains.Add(v);
                    break;
                }
                case "--no-h2" or "--cfproxy-h2-media":
                    noH2 = a == "--no-h2";
                    break;
                case "--no-cfproxy":
                    noCfproxy = true;
                    break;
                case "--no-secure":
                    noSecure = true;
                    break;
                case "--fake-tls-domain":
                {
                    string? v = Next();
                    if (v is null) return "после --fake-tls-domain нужен домен";
                    fakeTlsDomain = v.Trim();
                    break;
                }
                case "--ping-interval":
                {
                    string? v = Next();
                    if (v is null || !int.TryParse(v, NumberStyles.Integer, CultureInfo.InvariantCulture, out pingInterval))
                        return "после --ping-interval нужно число";
                    if (pingInterval < 0) return "--ping-interval не может быть отрицательным";
                    break;
                }
                case "--ping-jitter":
                {
                    string? v = Next();
                    if (v is null || !int.TryParse(v, NumberStyles.Integer, CultureInfo.InvariantCulture, out pingJitter))
                        return "после --ping-jitter нужно число";
                    if (pingJitter < 0) return "--ping-jitter не может быть отрицательным";
                    break;
                }
                case "--force-test-dc":
                    forceTestDc = true;
                    break;
                case "--proxy-protocol":
                    proxyProtocol = true;
                    break;
                default:
                    return $"неизвестный ключ: {a}";
            }
        }

        if (secret is null)
        {
            secret = EngineConfig.NewSecret();
        }
        else
        {
            secret = secret.Trim();
            if (!EngineConfig.IsValidSecret(secret)) return "секрет должен состоять ровно из 32 шестнадцатеричных символов";
        }

        Dictionary<int, string> redirects;
        if (!dcIpGiven)
        {
            redirects = new Dictionary<int, string> { [2] = "149.154.167.220", [4] = "149.154.167.220" };
        }
        else if (dcIp.Contains(null))
        {
            redirects = [];   // a bare --dc-ip means "the user cleared the list", not "use the defaults"
        }
        else
        {
            redirects = [];
            foreach (string entry in dcIp!)
            {
                int colon = entry!.IndexOf(':');
                if (colon <= 0
                    || !int.TryParse(entry[..colon], NumberStyles.Integer, CultureInfo.InvariantCulture, out int dc)
                    || !IPAddress.TryParse(entry[(colon + 1)..], out IPAddress? ip)
                    || ip.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork)
                    return $"неверный --dc-ip {entry}, ожидается DC:IP";
                redirects[dc] = ip.ToString();
            }
        }

        config = config with
        {
            Host = host,
            Port = port,
            Secret = secret,
            DcRedirects = redirects,
            Verbose = verbose,
            LogFile = logFile,
            LogMaxMb = logMaxMb,
            LogBackups = logBackups,
            BufferSize = Math.Max(4, bufKb) * 1024,
            PoolSize = Math.Max(0, poolSize),
            PingIntervalSeconds = pingInterval,
            PingJitterSeconds = pingJitter,
            CfproxyUserDomains = CoerceDomains(cfDomains),
            CfproxyWorkerDomains = CoerceDomains(workerDomains),
            CfproxyH2Media = !noH2,
            FallbackCfProxy = !noCfproxy,
            DisableSecure = noSecure,
            FakeTlsDomain = fakeTlsDomain,
            ForceTestDc = forceTestDc,
            ProxyProtocol = proxyProtocol,
        };
        return null;
    }

    /// <summary>Splits each entry on <c>, ; space</c>, trims, drops empties and case-insensitive duplicates.</summary>
    public static IReadOnlyList<string> CoerceDomains(IEnumerable<string> raw)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var result = new List<string>();
        foreach (string entry in raw)
        {
            foreach (string item in entry.Split([',', ';', ' ', '\t', '\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                if (seen.Add(item)) result.Add(item);
            }
        }
        return result;
    }
}
