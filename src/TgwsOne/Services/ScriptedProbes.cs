using TgwsOne.Core.Zapret;

namespace TgwsOne.Services;

/// <summary>
/// Dev only: <c>TGWS_FAKE_PROBES=1</c> makes the strategy finder use these instead of the network, so the visual checks are
/// repeatable and send nothing to Telegram or Cloudflare. Not a setting; nothing in the app turns it on. The answers follow the
/// strategy the service is on right now, like a real run, and mirror the example table of the spec.
/// </summary>
internal sealed class ScriptedProbes(IControlClient control) : ITcpProbe, ITlsProbe, IHttpProbe
{
    public const string Variable = "TGWS_FAKE_PROBES";

    private static readonly TimeSpan Latency = TimeSpan.FromMilliseconds(700);

    // strategy → (Telegram handshakes that get through, the download arrives whole, base time in ms)
    private static (int Telegram, bool Cloudflare, int Ms) Script(string strategy) => strategy switch
    {
        "fake-tls" => (5, true, 180),
        "split" => (5, true, 150),
        "split-stealth" => (3, false, 150),
        "aggressive" => (0, false, 0),
        _ => (4, true, 220),   // anything else, a custom strategy
    };

    /// <summary>The servers are always reachable: what the strategies change is the handshake, not the connection.</summary>
    public async Task<bool> ConnectAsync(string host, int port, TimeSpan timeout, CancellationToken ct)
    {
        await Task.Delay(Latency, ct);
        return true;
    }

    public async Task<TimeSpan?> HandshakeAsync(string host, int port, TimeSpan timeout, CancellationToken ct)
    {
        await Task.Delay(Latency, ct);
        var (telegram, _, ms) = Script(await CurrentAsync(ct));
        int index = int.Parse(host["kws".Length..host.IndexOf('.')]) - 1;   // kws1 → 0
        return index < telegram ? TimeSpan.FromMilliseconds(ms + 10 * index) : null;
    }

    public async Task<(bool Ok, TimeSpan Elapsed)> DownloadAsync(Uri url, long expectedBytes, TimeSpan timeout, CancellationToken ct)
    {
        await Task.Delay(Latency, ct);
        var (_, cloudflare, ms) = Script(await CurrentAsync(ct));
        return (cloudflare, TimeSpan.FromMilliseconds(ms));
    }

    private async Task<string> CurrentAsync(CancellationToken ct)
        => (await control.SendAsync(new ControlRequest(ControlCommands.Status), ct)).Status?.Strategy ?? "";
}
