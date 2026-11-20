namespace TgwsOne.Core.Zapret;

/// <summary>
/// The Linux side of interception: an own nftables table that queues the first packets of each TLS connection
/// to nfqws2, and the conntrack setting the reply-side rule needs. Everything lives in <c>table inet tgwsone</c>,
/// so removing it can't disturb anyone else's rules.
/// </summary>
public sealed class NftManager(ICommandRunner runner, ISysctl sysctl)
{
    private const string LiberalKey = "net.netfilter.nf_conntrack_tcp_be_liberal";

    // The value found before it was set to 1; only meaningful while _liberalSet.
    private string? _savedLiberal;
    private bool _liberalSet;

    /// <summary>
    /// <c>bypass</c> lets traffic through when nfqws2 isn't running. Packets carrying the desync mark skip the queue
    /// (nfqws2's own fakes would loop), and are excluded from conntrack before defragmentation.
    /// </summary>
    public static string Ruleset(int queueNum) => $$"""
        table inet tgwsone {
          chain post {
            type filter hook postrouting priority 101; policy accept;
            meta mark & 0x40000000 == 0 tcp dport 443 ct original packets 1-12 queue num {{queueNum}} bypass
          }
          chain pre {
            type filter hook prerouting priority -101; policy accept;
            meta mark & 0x40000000 == 0 tcp sport 443 ct reply packets 1-12 queue num {{queueNum}} bypass
          }
          chain predefrag {
            type filter hook output priority -401; policy accept;
            meta mark & 0x40000000 != 0 notrack
          }
        }

        """.ReplaceLineEndings("\n");

    /// <summary>
    /// Installs the table in one atomic transaction (declaring it first makes the following delete safe on a first
    /// run), then sets <c>nf_conntrack_tcp_be_liberal=1</c>, remembering the old value for <see cref="RemoveAsync"/>.
    /// </summary>
    /// <exception cref="InvalidOperationException">nft refused the rules; the message carries its output.</exception>
    public async Task ApplyAsync(CancellationToken ct = default)
    {
        string script = "table inet tgwsone\ndelete table inet tgwsone\n" + Ruleset(ZapretArgs.QueueNum);
        (int code, string output) = await runner.RunAsync("nft", ["-f", "-"], script, ct);
        if (code != 0) throw new InvalidOperationException($"nft: {output.Trim()}");
        SetLiberal();
    }

    /// <summary>Deletes the table (already gone is fine) and puts the sysctl back, even when the delete fails.</summary>
    /// <exception cref="InvalidOperationException">nft failed for a reason other than a missing table.</exception>
    public async Task RemoveAsync(CancellationToken ct = default)
    {
        try
        {
            (int code, string output) = await runner.RunAsync("nft", ["delete", "table", "inet", "tgwsone"], null, ct);
            if (code != 0 && !output.Contains("No such file or directory", StringComparison.Ordinal))
                throw new InvalidOperationException($"nft: {output.Trim()}");
        }
        finally
        {
            RestoreLiberal();
        }
    }

    // Only an optimisation for reply-side matching, and the conntrack module may not even exist: never fail the start for it.
    private void SetLiberal()
    {
        if (_liberalSet) return;   // already ours; the saved value is still the one from before us
        try
        {
            string? old = sysctl.Read(LiberalKey);
            if (old == null || old == "1") return;
            sysctl.Write(LiberalKey, "1");
            _savedLiberal = old;
            _liberalSet = true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    private void RestoreLiberal()
    {
        if (!_liberalSet) return;
        _liberalSet = false;
        try { sysctl.Write(LiberalKey, _savedLiberal!); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }
}
