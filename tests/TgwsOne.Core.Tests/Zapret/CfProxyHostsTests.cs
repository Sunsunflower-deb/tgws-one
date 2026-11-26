using TgwsOne.Core.Zapret;

namespace TgwsOne.Core.Tests.Zapret;

public class CfProxyHostsTests
{
    [Fact]
    public void Expand_ListsTheDomainAndEveryDataCentreHost()
    {
        Assert.Equal(
        [
            "pclead.co.uk",
            "kws1.pclead.co.uk",
            "kws2.pclead.co.uk",
            "kws3.pclead.co.uk",
            "kws4.pclead.co.uk",
            "kws5.pclead.co.uk",
        ], CfProxyHosts.Expand(["pclead.co.uk"]));
    }

    [Fact]
    public void Expand_TrimsAndLowerCases_SkipsCommentsAndBlanks_DropsRepeats()
    {
        IReadOnlyList<string> hosts = CfProxyHosts.Expand(["  PCLead.co.uk ", "", "# comment", "pclead.co.uk.", "offshor.co.uk"]);

        Assert.Equal("pclead.co.uk", hosts[0]);
        Assert.Equal(6, hosts.Count(h => h.EndsWith("pclead.co.uk", StringComparison.Ordinal)));
        Assert.Contains("kws3.offshor.co.uk", hosts);
        Assert.DoesNotContain(hosts, h => h.Contains(' ') || h.StartsWith('#'));
        Assert.Equal(hosts.Count, hosts.Distinct(StringComparer.OrdinalIgnoreCase).Count());
    }

    [Fact]
    public void Expand_Empty_IsEmpty()
        => Assert.Empty(CfProxyHosts.Expand([]));

    [Fact]
    public void Write_ReadsTheDomainFile_AndWritesTheHostlist()
    {
        using var tmp = new TempDir();
        string domains = tmp.Combine("cfproxy-domains.txt");
        string hosts = tmp.Combine("cfproxy-hosts.txt");
        File.WriteAllText(domains, "# domains\npclead.co.uk\nnot a host at all\n");

        int count = CfProxyHosts.Write(domains, hosts);

        // "not a host at all" has spaces and one label: validation drops it before it reaches a SYSTEM process.
        Assert.Equal(6, count);
        IReadOnlyList<string> written = Exclusions.Read(hosts);
        Assert.All(File.ReadAllLines(hosts).Take(CfProxyHosts.Header.Split('\n').Length),
            line => Assert.StartsWith("# ", line));
        Assert.Contains("kws4.pclead.co.uk", written);
        Assert.DoesNotContain(written, h => h.Contains(' '));
    }

    [Fact]
    public void Write_WithoutADomainFile_WritesAHostlistWithNoEntries()
    {
        using var tmp = new TempDir();
        string hosts = tmp.Combine("cfproxy-hosts.txt");

        Assert.Equal(0, CfProxyHosts.Write(tmp.Combine("missing.txt"), hosts));

        Assert.True(File.Exists(hosts));
        Assert.Empty(Exclusions.Read(hosts));
    }
}
