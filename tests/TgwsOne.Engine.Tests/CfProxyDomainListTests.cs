using TgwsOne.Core.Zapret;
using TgwsOne.Engine.Transport;

namespace TgwsOne.Engine.Tests;

/// <summary>
/// The narrow bypass scope protects the proxy's Cloudflare fallback with a hostlist the service builds from
/// <c>lists/cfproxy-domains.txt</c>. Those are exactly the domains the engine dials, so a shipped list that drifted from
/// <see cref="DomainBalancer.DefaultDomains"/> would leave the fallback — the route that works when the direct one is
/// blocked — silently uncovered.
/// </summary>
public class CfProxyDomainListTests
{
    [Fact]
    public void ShippedDomainList_HoldsExactlyTheEngineDefaults()
    {
        string file = Path.Combine(RepoRoot(), "lists", CfProxyHosts.DomainsFile);

        Assert.True(File.Exists(file), $"нет файла списка доменов: {file}");

        Assert.Equal(DomainBalancer.DefaultDomains, Exclusions.Read(file));
    }

    [Fact]
    public void ShippedDomainList_ExpandsToHostsTheEngineActuallyDials()
    {
        string file = Path.Combine(RepoRoot(), "lists", CfProxyHosts.DomainsFile);

        IReadOnlyList<string> hosts = CfProxyHosts.Expand(Exclusions.Read(file));

        // ProxyEngine dials $"kws{dc}.{baseDomain}" for the CF fallback, H2Media the same host over HTTP/2.
        foreach (string domain in DomainBalancer.DefaultDomains)
            Assert.Contains($"kws2.{domain}", hosts);
        Assert.Equal(DomainBalancer.DefaultDomains.Count * (CfProxyHosts.Dcs.Count + 1), hosts.Count);
    }

    private static string RepoRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir != null; dir = dir.Parent)
            if (File.Exists(Path.Combine(dir.FullName, "zapret2", "zapret2.lock"))) return dir.FullName;
        throw new InvalidOperationException("не найден корень репозитория (zapret2/zapret2.lock)");
    }
}
