using System.Runtime.CompilerServices;
using TgwsOne.Engine.Desync;

namespace TgwsOne.Mobile;

/// <summary>
/// Tries the strategies of the current operator one by one on the real bypass and measures what each does for
/// Telegram, the CF-proxy route and the engine's own end-to-end exchange. It changes the bypass for the running
/// engine while it runs, so it is only ever started on request; <see cref="RunAsync"/> puts everything back, also
/// when the run is cancelled.
/// </summary>
public sealed class DesyncFinder
{
    public const string NoBypassId = "none";
    public const string AutoId = "auto";

    public static readonly IReadOnlyList<string> TelegramHosts =
        [.. Enumerable.Range(1, 5).Select(i => $"kws{i}.web.telegram.org")];

    public static readonly Uri CloudflareUrl = new("https://speed.cloudflare.com/__down?bytes=65536");
    public const long CloudflareBytes = 65536;

    /// <summary>The engine's time to drop its pools and warm the next connections before they are measured.</summary>
    public static readonly TimeSpan SettleTime = TimeSpan.FromMilliseconds(1500);
    public static readonly TimeSpan TlsTimeout = TimeSpan.FromSeconds(5);
    public static readonly TimeSpan DownloadTimeout = TimeSpan.FromSeconds(10);

    /// <summary>The fake strategies are tried with each of these TTLs; a fake that reaches the server teaches nothing.</summary>
    public static readonly IReadOnlyList<int> FakeTtls = [2, 4, 8, 12];

    public const string NoTelegramVerdict =
        "Ни одна стратегия не дала связи с серверами Telegram: похоже, провайдер блокирует Telegram по IP-адресу. Здесь поможет только VPN.";

    public const string NoBypassNeededVerdict =
        "Без обхода всё проходит: похоже, на этой сети Telegram не блокируется.";

    private readonly IDesyncControl _control;
    private readonly IEgressProbes _probes;
    private readonly Func<TimeSpan, CancellationToken, Task> _delay;

    /// <param name="delay">Replaces the wait for the engine to settle (tests).</param>
    public DesyncFinder(IDesyncControl control, IEgressProbes probes,
                        Func<TimeSpan, CancellationToken, Task>? delay = null)
    {
        _control = control;
        _probes = probes;
        _delay = delay ?? Task.Delay;
    }

    /// <summary>What to try: the current operator's rotation, with every fake strategy multiplied by <see cref="FakeTtls"/>.</summary>
    public IReadOnlyList<string> Candidates() => Expand(_control.Rotation);

    /// <summary>
    /// Keeps the rotation's order. A strategy with a <see cref="Fake"/> step becomes one candidate per TTL,
    /// written <c>id@ttl</c>; every other strategy is one candidate under its own id.
    /// </summary>
    public static IReadOnlyList<string> Expand(IReadOnlyList<DesyncStrategy> rotation)
    {
        ArgumentNullException.ThrowIfNull(rotation);
        var candidates = new List<string>();
        foreach (DesyncStrategy strategy in rotation)
        {
            if (strategy.Steps.Any(step => step is Fake))
            {
                foreach (int ttl in FakeTtls) candidates.Add($"{strategy.Id}@{ttl}");
            }
            else
            {
                candidates.Add(strategy.Id);
            }
        }
        return candidates;
    }

    /// <summary>Splits <c>id@ttl</c>; a plain id has no TTL override.</summary>
    public static bool TryParseCandidate(string candidate, out string strategyId, out int? ttl)
    {
        ArgumentNullException.ThrowIfNull(candidate);
        int at = candidate.LastIndexOf('@');
        if (at > 0 && int.TryParse(candidate[(at + 1)..], out int value) && value > 0)
        {
            strategyId = candidate[..at];
            ttl = value;
            return strategyId.Length > 0;
        }
        strategyId = candidate;
        ttl = null;
        return candidate.Length > 0;
    }

    /// <summary>The same strategy with every fake step's TTL replaced.</summary>
    public static DesyncStrategy WithFakeTtl(DesyncStrategy strategy, int ttl)
        => strategy with
        {
            Steps = [.. strategy.Steps.Select(step => step is Fake fake ? fake with { Ttl = ttl } : step)],
        };

    public Task<FinderBaseline> CaptureAsync(CancellationToken ct = default) => _control.CaptureAsync(ct);

    /// <summary>
    /// The baseline row «без обхода» first, then every candidate in order. Each candidate is selected, the engine
    /// gets <see cref="SettleTime"/> to drop its idle pools, and the probes run side by side. The baseline is put
    /// back in a <c>finally</c>, so a cancelled run cannot leave the user on a strategy they did not choose.
    /// </summary>
    public async IAsyncEnumerable<FinderRow> RunAsync(IReadOnlyList<string> candidates, FinderBaseline baseline,
                                                      [EnumeratorCancellation] CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(candidates);
        try
        {
            string? noneProblem = await _control.ApplyAsync(NoBypassId, ct);
            if (noneProblem is { } problem)
            {
                yield return new FinderRow(NoBypassId, 0, false, false, null, problem);
            }
            else
            {
                await _delay(SettleTime, ct);
                yield return await MeasureAsync(NoBypassId, ct);
            }

            foreach (string id in candidates)
            {
                ct.ThrowIfCancellationRequested();
                string? error = await _control.ApplyAsync(id, ct);
                if (error is not null)
                {
                    yield return new FinderRow(id, 0, false, false, null, error);
                    continue;
                }

                await _delay(SettleTime, ct);
                yield return await MeasureAsync(id, ct);
            }
        }
        finally
        {
            using var restore = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            try { await _control.RestoreAsync(baseline, restore.Token); }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                // Restoring is best effort here; the public RestoreAsync reports the problem to the caller.
            }
        }
    }

    private async Task<FinderRow> MeasureAsync(string id, CancellationToken ct)
    {
        Task<TimeSpan?>[] handshakes = [.. TelegramHosts.Select(h => _probes.BypassHandshakeAsync(h, 443, "ws", TlsTimeout, ct))];
        Task<TimeSpan?> cloudflare = _probes.CloudflareDomain.Length == 0
            ? Task.FromResult<TimeSpan?>(null)
            : _probes.BypassHandshakeAsync($"kws1.{_probes.CloudflareDomain}", 443, "cf-proxy", TlsTimeout, ct);
        Task<bool> selfCheck = _probes.SelfCheckAsync(TlsTimeout, ct);

        TimeSpan?[] times = await Task.WhenAll(handshakes);
        TimeSpan? cloudflareTime = await cloudflare;
        bool selfCheckOk = await selfCheck;

        List<TimeSpan> got = [.. times.Where(t => t is not null).Select(t => t!.Value).Order()];
        return new FinderRow(id, got.Count, cloudflareTime is not null, selfCheckOk,
                             got.Count == 0 ? null : Median(got), null);
    }

    // The middle value; of an even number the lower of the two middle ones, so the figure is one that was really measured.
    private static TimeSpan Median(List<TimeSpan> sorted) => sorted[(sorted.Count - 1) / 2];

    /// <summary>
    /// The best row by the spec's scoring: most Telegram handshakes, then the end-to-end check, then the lowest
    /// median; equal rows keep the order they were tried in. Null when not one candidate reached Telegram.
    /// </summary>
    public static FinderRow? Best(IReadOnlyList<FinderRow> rows)
    {
        ArgumentNullException.ThrowIfNull(rows);
        FinderRow? best = rows
            .OrderByDescending(r => r.TelegramOk)
            .ThenByDescending(r => r.SelfCheckOk)
            .ThenBy(r => r.Median ?? TimeSpan.MaxValue)
            .FirstOrDefault();
        return best is { TelegramOk: > 0 } ? best : null;
    }

    /// <summary>True when the baseline row itself got every Telegram handshake and the engine exchange through.</summary>
    public static bool NoBypassNeeded(FinderRow baseline)
        => baseline.TelegramOk == TelegramHosts.Count && baseline.SelfCheckOk;

    /// <summary>Puts the bypass back as it was. Returns the engine's message when something could not be put back.</summary>
    public Task<string?> RestoreAsync(FinderBaseline baseline, CancellationToken ct = default)
        => _control.RestoreAsync(baseline, ct);

    /// <summary>Makes <paramref name="candidateId"/> the choice. Returns the engine's message when it refuses.</summary>
    public Task<string?> ApplyAsync(string candidateId, CancellationToken ct = default)
        => _control.ApplyAsync(candidateId, ct);
}
