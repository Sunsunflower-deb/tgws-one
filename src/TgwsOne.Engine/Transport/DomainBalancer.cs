using System.Net;
using System.Text;

namespace TgwsOne.Engine.Transport;

/// <summary>
/// The Cloudflare-proxied base domains behind which Telegram's WebSocket hosts can be reached when the direct
/// route is blocked. One active domain per DC; a connection that worked makes its domain that DC's preferred one,
/// the others are tried in random order. The default list is the upstream list, lightly obfuscated so that a
/// plain-text search does not find every domain in one place.
/// </summary>
public sealed class DomainBalancer
{
    private const string Suffix = ".co.uk";

    // Verbatim from upstream proxy/config.py (v1.11.4), to be decoded exactly as it decodes them.
    private static readonly string[] EncodedDefaults =
    [
        "virkgj.com", "vmmzovy.com", "mkuosckvso.com", "zaewayzmplad.com", "twdmbzcm.com",
        "awzwsldi.com", "clngqrflngqin.com", "tjacxbqtj.com", "bxaxtxmrw.com", "dmohrsgmohcrwb.com",
        "vwbmtmoi.com", "khgrre.com", "ulihssf.com", "tmhqsdqmfpmk.com", "xwuwoqbm.com",
        "orgcnunpj.com", "zhkuldz.com", "zypoljnslxa.com", "efabnxaowuzs.com", "zaftuzsftqdq.com",
    ];

    public static readonly IReadOnlyList<string> DefaultDomains =
        EncodedDefaults.Select(DecodeDomain).ToArray();

    private static readonly int[] Dcs = [1, 2, 3, 4, 5, 203];
    private readonly object _gate = new();
    private List<string> _domains = [];
    private readonly Dictionary<int, string> _dcToDomain = [];

    public DomainBalancer() => UpdateDomainsList(DefaultDomains);

    /// <summary>Undoes the upstream obfuscation: shift every letter back by the number of letters before it.</summary>
    internal static string DecodeDomain(string encoded)
    {
        if (!encoded.EndsWith(".com", StringComparison.Ordinal)) return encoded;
        string body = encoded[..^4];
        int letters = body.Count(char.IsLetter);
        var result = new StringBuilder(body.Length + Suffix.Length);
        foreach (char c in body)
        {
            if (char.IsLetter(c))
            {
                int lower = c > '`' ? 'a' : 'A';
                int shifted = (c - lower - letters) % 26;
                if (shifted < 0) shifted += 26;
                result.Append((char)(shifted + lower));
            }
            else
            {
                result.Append(c);
            }
        }
        result.Append(Suffix);
        return result.ToString();
    }

    public IReadOnlyList<string> Domains
    {
        get { lock (_gate) return _domains.ToArray(); }
    }

    public void UpdateDomainsList(IReadOnlyList<string> domains)
    {
        lock (_gate)
        {
            if (_domains.Count == domains.Count && _domains.OrderBy(d => d).SequenceEqual(domains.OrderBy(d => d)))
                return;

            _domains = [.. domains];
            _dcToDomain.Clear();
            if (_domains.Count == 0) return;
            foreach (int dc in Dcs) _dcToDomain[dc] = _domains[Random.Shared.Next(_domains.Count)];
        }
    }

    /// <summary>Records the domain a successful connection used; true when that changed the preferred one.</summary>
    public bool UpdateDomainForDc(int dc, string domain)
    {
        lock (_gate)
        {
            if (_dcToDomain.GetValueOrDefault(dc) == domain) return false;
            _dcToDomain[dc] = domain;
            return true;
        }
    }

    /// <summary>The preferred domain first, then the rest in random order.</summary>
    public IReadOnlyList<string> GetDomainsForDc(int dc)
    {
        lock (_gate)
        {
            string? current = _dcToDomain.GetValueOrDefault(dc);
            var rest = _domains.Where(d => d != current).ToList();
            Shuffle(rest);
            return current is null ? rest : [current, .. rest];
        }
    }

    private static void Shuffle<T>(IList<T> items)
    {
        for (int i = items.Count - 1; i > 0; i--)
        {
            int j = Random.Shared.Next(i + 1);
            (items[i], items[j]) = (items[j], items[i]);
        }
    }
}

/// <summary>
/// Keeps the balancer's list fresh from the upstream GitHub list. User-supplied domains win and nothing is fetched;
/// otherwise the built-in list is used at once and a background task refreshes it hourly. A fetch that fails or
/// returns junk keeps the current list (the upstream rule: at least three valid domains).
/// </summary>
public sealed class CfDomainRefresher
{
    public const string ListUrl =
        "https://raw.githubusercontent.com/Flowseal/tg-ws-proxy/main/.github/cfproxy-domains.txt";
    private const string PinnedIp = "185.199.109.133";
    private const int MinimumValidDomains = 3;

    private readonly DomainBalancer _balancer;
    private readonly EngineLog _log;
    private readonly IReadOnlyList<string> _userDomains;
    private readonly CancellationTokenSource _stop = new();
    private Task? _loop;

    public CfDomainRefresher(DomainBalancer balancer, EngineLog log, IReadOnlyList<string> userDomains)
    {
        _balancer = balancer;
        _log = log;
        _userDomains = userDomains;
    }

    public void Start()
    {
        if (_userDomains.Count > 0)
        {
            _balancer.UpdateDomainsList(_userDomains);
            return;
        }
        _balancer.UpdateDomainsList(DomainBalancer.DefaultDomains);
        _loop = Task.Run(() => RunAsync(_stop.Token));
    }

    private async Task RunAsync(CancellationToken ct)
    {
        await RefreshAsync(ct).ConfigureAwait(false);
        using var timer = new PeriodicTimer(TimeSpan.FromHours(1));
        try
        {
            while (await timer.WaitForNextTickAsync(ct).ConfigureAwait(false))
                await RefreshAsync(ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // shutting down
        }
    }

    internal async Task RefreshAsync(CancellationToken ct)
    {
        List<string> fetched = await FetchAsync(ct).ConfigureAwait(false);
        var valid = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (string domain in fetched)
        {
            string item = domain.Trim().ToLowerInvariant();
            if (!IsValidDomain(item) || !seen.Add(item)) continue;
            valid.Add(item);
        }

        if (valid.Count >= MinimumValidDomains)
        {
            _balancer.UpdateDomainsList(valid);
            _log.Info($"CF proxy domain pool updated from GitHub ({valid.Count} domains)");
        }
        else if (fetched.Count > 0)
        {
            _log.Warn($"Ignoring fetched CF proxy domains due to low-quality payload (total={fetched.Count}, " +
                      $"valid={valid.Count}, required>={MinimumValidDomains}); keeping current domain pool");
        }
        else
        {
            _log.Warn("CF proxy domain refresh failed or empty response; keeping current domain pool");
        }
    }

    private async Task<List<string>> FetchAsync(CancellationToken ct)
    {
        string url = $"{ListUrl}?{RandomString(7)}";
        foreach (bool pinned in new[] { false, true })
        {
            try
            {
                using HttpClient http = CreateClient(pinned, TimeSpan.FromSeconds(10));
                using var request = new HttpRequestMessage(HttpMethod.Get, url);
                request.Headers.UserAgent.ParseAdd("tg-ws-proxy");
                using HttpResponseMessage response = await http.SendAsync(request, ct).ConfigureAwait(false);
                response.EnsureSuccessStatusCode();
                string text = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
                var domains = new List<string>();
                foreach (string raw in text.Split('\n'))
                {
                    string line = raw.Trim();
                    if (line.Length == 0 || line.StartsWith('#')) continue;
                    domains.Add(DomainBalancer.DecodeDomain(line));
                }
                return domains;
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or IOException)
            {
                if (ct.IsCancellationRequested) break;
                _log.Debug($"CF proxy domain fetch{(pinned ? " via pinned IP" : "")} failed: {ex.Message}");
            }
        }
        return [];
    }

    internal static HttpClient CreateClient(bool pin, TimeSpan timeout)
    {
        var handler = new SocketsHttpHandler
        {
            UseProxy = false,
            ConnectTimeout = timeout,
            AutomaticDecompression = DecompressionMethods.None,
        };
        if (pin)
        {
            handler.ConnectCallback = async (context, ct) =>
            {
                var socket = new System.Net.Sockets.Socket(System.Net.Sockets.SocketType.Stream, System.Net.Sockets.ProtocolType.Tcp);
                try
                {
                    await socket.ConnectAsync(IPAddress.Parse(PinnedIp), context.DnsEndPoint.Port, ct).ConfigureAwait(false);
                    return new System.Net.Sockets.NetworkStream(socket, ownsSocket: true);
                }
                catch
                {
                    socket.Dispose();
                    throw;
                }
            };
        }
        return new HttpClient(handler) { Timeout = timeout };
    }

    internal static bool IsValidDomain(string domain)
    {
        if (domain.Length is 0 or > 253) return false;
        if (domain.StartsWith('.') || domain.EndsWith('.')) return false;
        string[] labels = domain.Split('.');
        if (labels.Length < 2) return false;
        foreach (string label in labels)
        {
            if (label.Length is 0 or > 63) return false;
            if (label[0] == '-' || label[^1] == '-') return false;
            if (!label.All(c => char.IsAsciiLetterOrDigit(c) || c == '-')) return false;
        }
        string tld = labels[^1];
        return tld.Length >= 2 && tld.Any(char.IsAsciiLetter);
    }

    private static string RandomString(int length)
    {
        const string alphabet = "abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ";
        Span<char> buffer = stackalloc char[length];
        for (int i = 0; i < length; i++) buffer[i] = alphabet[Random.Shared.Next(alphabet.Length)];
        return new string(buffer);
    }

    public void Dispose()
    {
        _stop.Cancel();
        try { _loop?.Wait(TimeSpan.FromSeconds(2)); }
        catch (AggregateException) { }
        _stop.Dispose();
    }
}
