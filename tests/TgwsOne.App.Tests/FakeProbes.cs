using TgwsOne.Services;

namespace TgwsOne.Tests;

/// <summary>What a strategy does to the network in a test: how many of the five Telegram handshakes get through, whether the download arrives whole, and how long it all takes.</summary>
internal sealed record Outcome(int TelegramOk, bool CloudflareOk, TimeSpan Time);

/// <summary>
/// Both probes in one: every answer is looked up by the strategy the fake service is on <em>right now</em>, which is how a real
/// run behaves (the strategy decides what gets through). A strategy without an entry lets nothing through.
/// </summary>
internal sealed class FakeProbes(FakeBypassService service) : ITlsProbe, IHttpProbe
{
    private readonly object _gate = new();
    private readonly List<(string Host, int Port, TimeSpan Timeout)> _handshakes = [];
    private readonly List<(Uri Url, long Bytes, TimeSpan Timeout)> _downloads = [];

    public Dictionary<string, Outcome> Outcomes { get; } = [];

    /// <summary>Runs first in every probe (to hold probes back, or to count how many are in flight).</summary>
    public Func<CancellationToken, Task>? BeforeProbe { get; set; }

    public IReadOnlyList<(string Host, int Port, TimeSpan Timeout)> Handshakes { get { lock (_gate) return [.. _handshakes]; } }
    public IReadOnlyList<(Uri Url, long Bytes, TimeSpan Timeout)> Downloads { get { lock (_gate) return [.. _downloads]; } }

    private Outcome Now => Outcomes.GetValueOrDefault(service.Strategy) ?? new Outcome(0, false, TimeSpan.Zero);

    public async Task<TimeSpan?> HandshakeAsync(string host, int port, TimeSpan timeout, CancellationToken ct)
    {
        lock (_gate) _handshakes.Add((host, port, timeout));
        if (BeforeProbe is { } hold) await hold(ct);
        ct.ThrowIfCancellationRequested();

        int index = int.Parse(host["kws".Length..host.IndexOf('.')]) - 1;   // kws1 → 0
        Outcome o = Now;
        return index < o.TelegramOk ? o.Time + TimeSpan.FromMilliseconds(10 * index) : null;
    }

    public async Task<(bool Ok, TimeSpan Elapsed)> DownloadAsync(Uri url, long expectedBytes, TimeSpan timeout, CancellationToken ct)
    {
        lock (_gate) _downloads.Add((url, expectedBytes, timeout));
        if (BeforeProbe is { } hold) await hold(ct);
        ct.ThrowIfCancellationRequested();

        Outcome o = Now;
        return (o.CloudflareOk, o.Time);
    }
}
