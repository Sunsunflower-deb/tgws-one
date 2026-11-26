namespace TgwsOne.Core.Zapret;

/// <summary>
/// The host names the proxy itself opens over TLS when it falls back to Cloudflare, as a hostlist nfqws2/winws2 can
/// match. The narrow bypass scope targets <c>telegram.txt</c>, but the proxy's fallback does not go to those hosts: it
/// dials <c>kws&lt;dc&gt;.&lt;domain&gt;</c> on Cloudflare, so the domain pool of the CF-proxy has to be covered explicitly,
/// or the fallback is the one route the bypass never touches.
/// </summary>
public static class CfProxyHosts
{
    /// <summary>Admin-owned list of the base domains to expand; shipped with the application, seeded once by the service.</summary>
    public const string DomainsFile = "cfproxy-domains.txt";

    /// <summary>Generated hostlist the narrow scope bypasses; rewritten from <see cref="DomainsFile"/> on every run.</summary>
    public const string HostsFile = "cfproxy-hosts.txt";

    /// <summary>Telegram's WebSocket hosts behind Cloudflare are <c>kws1</c>…<c>kws5</c> (DC 203 uses DC 2's hosts).</summary>
    public static readonly IReadOnlyList<int> Dcs = [1, 2, 3, 4, 5];

    public const string Header =
        "Сгенерировано службой TgwsOne: узлы Cloudflare, через которые прокси ходит в Telegram.\n" +
        "Файл перезаписывается при каждом запуске обхода — правьте cfproxy-domains.txt.";

    /// <summary>
    /// Every host name to bypass for the given base domains: the domain itself and <c>kws&lt;dc&gt;.&lt;domain&gt;</c> for each
    /// data centre. Entries are compared with the ClientHello name as written, so no wildcard is relied on. Repeat
    /// entries are dropped and the input order is kept.
    /// </summary>
    public static IReadOnlyList<string> Expand(IEnumerable<string> domains)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var hosts = new List<string>();
        foreach (string raw in domains)
        {
            string domain = raw.Trim().TrimEnd('.').ToLowerInvariant();
            if (domain.Length == 0 || domain[0] == '#') continue;
            if (seen.Add(domain)) hosts.Add(domain);
            foreach (int dc in Dcs)
            {
                string host = $"kws{dc}.{domain}";
                if (seen.Add(host)) hosts.Add(host);
            }
        }
        return hosts;
    }

    /// <summary>
    /// Rewrites <paramref name="hostsFile"/> from <paramref name="domainsFile"/> and returns the number of hosts.
    /// A missing domain list leaves an empty hostlist behind, which the caller must treat as "nothing to cover".
    /// </summary>
    public static int Write(string domainsFile, string hostsFile)
    {
        // The file decides what a SYSTEM service hands to a SYSTEM process, so the entries are validated like the
        // exclusions are, not taken on trust.
        (IReadOnlyList<string> domains, _) = Exclusions.NormalizeHosts(Exclusions.Read(domainsFile));
        IReadOnlyList<string> hosts = Expand(domains);
        Exclusions.Write(hostsFile, hosts, Header);
        return hosts.Count;
    }
}
