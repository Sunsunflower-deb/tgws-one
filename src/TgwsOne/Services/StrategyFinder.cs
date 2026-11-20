using System.Runtime.CompilerServices;
using TgwsOne.Core.Zapret;

namespace TgwsOne.Services;

/// <summary>How one candidate did: Telegram handshakes that got through (of five), whether the Cloudflare download arrived whole, the median handshake time, or why it could not be tried.</summary>
internal sealed record FinderRow(string StrategyId, int TelegramOk, bool CloudflareOk, TimeSpan? Median, string? Error);

/// <summary>What the bypass was before the run: this is what «Вернуть как было» puts back.</summary>
internal sealed record FinderBaseline(string Strategy, bool Enabled);

/// <summary>The finder could not even start (the service refused to switch on, say); the message is for the user.</summary>
internal sealed class FinderException(string message) : Exception(message);

/// <summary>
/// Tries the strategies one by one on the real bypass and measures what each does for Telegram and for Cloudflare, which
/// zapret users know as the "freeze after 16 KB" throttling. It changes the bypass for everyone on the machine while it runs,
/// so it is only ever started on request, and <see cref="RestoreAsync"/> puts everything back.
/// </summary>
internal sealed class StrategyFinder
{
    public static readonly IReadOnlyList<string> TelegramHosts =
        [.. Enumerable.Range(1, 5).Select(i => $"kws{i}.web.telegram.org")];

    public static readonly Uri CloudflareUrl = new("https://speed.cloudflare.com/__down?bytes=65536");
    public const long CloudflareBytes = 65536;

    /// <summary>The child needs a moment to be up before its traffic is worth measuring.</summary>
    public static readonly TimeSpan SettleTime = TimeSpan.FromMilliseconds(1500);
    public static readonly TimeSpan TlsTimeout = TimeSpan.FromSeconds(5);
    public static readonly TimeSpan DownloadTimeout = TimeSpan.FromSeconds(10);

    public const string NoTelegramVerdict =
        "Ни одна стратегия не дала связи с серверами Telegram: похоже, провайдер блокирует Telegram по IP-адресу. Здесь поможет только VPN.";

    private readonly IControlClient _control;
    private readonly ITlsProbe _tls;
    private readonly IHttpProbe _http;
    private readonly Func<TimeSpan, CancellationToken, Task> _delay;

    /// <param name="delay">Replaces the wait for the child to start (tests).</param>
    public StrategyFinder(IControlClient control, ITlsProbe tls, IHttpProbe http, Func<TimeSpan, CancellationToken, Task>? delay = null)
    {
        _control = control;
        _tls = tls;
        _http = http;
        _delay = delay ?? Task.Delay;
    }

    /// <summary>What to try: the four strategies <c>auto</c> rotates through, then the administrator's own, in the order the service lists them.</summary>
    public async Task<IReadOnlyList<string>> CandidatesAsync(CancellationToken ct)
    {
        ControlResponse r = await _control.SendAsync(new ControlRequest(ControlCommands.Strategies), ct);
        if (!r.Ok) throw new FinderException(r.Error ?? "служба не назвала стратегии");
        var listed = (r.Items ?? []).ToList();
        IEnumerable<string> builtIn = BuiltInStrategies.Rotation.Where(id => listed.Any(i => i.Id == id));
        IEnumerable<string> custom = listed.Where(i => i.Kind == "custom").Select(i => i.Id);
        return [.. builtIn, .. custom];
    }

    /// <exception cref="ServiceUnavailableException">There is no service.</exception>
    public async Task<FinderBaseline> CaptureAsync(CancellationToken ct)
    {
        ControlResponse r = await _control.SendAsync(new ControlRequest(ControlCommands.Status), ct);
        return r.Status is { } s
            ? new FinderBaseline(s.Strategy, s.Enabled)
            : throw new FinderException(r.Error ?? "служба не сообщила состояние");
    }

    /// <summary>
    /// Steps of the spec for each candidate: <c>use</c> it (a refusal makes an error row and the next one follows), wait for the
    /// child, then five TLS handshakes and the download side by side. A bypass that was off is switched on first: with it off
    /// <c>use</c> only remembers the choice and nothing would be measured.
    /// </summary>
    /// <exception cref="FinderException">The bypass could not be switched on.</exception>
    public async IAsyncEnumerable<FinderRow> RunAsync(IReadOnlyList<string> candidates, FinderBaseline baseline,
                                                      [EnumeratorCancellation] CancellationToken ct)
    {
        if (!baseline.Enabled)
        {
            ControlResponse on = await _control.SendAsync(new ControlRequest(ControlCommands.On), ct);
            if (!on.Ok) throw new FinderException($"не удалось включить обход DPI: {on.Error}");
        }

        foreach (string id in candidates)
        {
            ct.ThrowIfCancellationRequested();
            ControlResponse use = await _control.SendAsync(new ControlRequest(ControlCommands.Use, Strategy: id), ct);
            if (!use.Ok)
            {
                yield return new FinderRow(id, 0, false, null, use.Error);
                continue;
            }

            await _delay(SettleTime, ct);
            yield return await MeasureAsync(id, ct);
        }
    }

    private async Task<FinderRow> MeasureAsync(string id, CancellationToken ct)
    {
        Task<TimeSpan?>[] handshakes = [.. TelegramHosts.Select(h => _tls.HandshakeAsync(h, 443, TlsTimeout, ct))];
        Task<(bool Ok, TimeSpan Elapsed)> download = _http.DownloadAsync(CloudflareUrl, CloudflareBytes, DownloadTimeout, ct);

        TimeSpan?[] times = await Task.WhenAll(handshakes);
        (bool cloudflareOk, _) = await download;

        List<TimeSpan> got = [.. times.Where(t => t is not null).Select(t => t!.Value).Order()];
        return new FinderRow(id, got.Count, cloudflareOk, got.Count == 0 ? null : Median(got), null);
    }

    // The middle value; of an even number the lower of the two middle ones, so the figure is one that was really measured.
    private static TimeSpan Median(List<TimeSpan> sorted) => sorted[(sorted.Count - 1) / 2];

    /// <summary>
    /// The best of the rows by the spec's scoring: most Telegram handshakes, then the Cloudflare download whole, then the lowest
    /// median time; equal rows keep the order they were tried in. Null when not one candidate reached Telegram.
    /// </summary>
    public static FinderRow? Best(IReadOnlyList<FinderRow> rows)
    {
        FinderRow? best = rows
            .OrderByDescending(r => r.TelegramOk)
            .ThenByDescending(r => r.CloudflareOk)
            .ThenBy(r => r.Median ?? TimeSpan.MaxValue)
            .FirstOrDefault();
        return best is { TelegramOk: > 0 } ? best : null;
    }

    /// <summary>Puts the bypass back as it was: the strategy it had and, if it was off, off. Returns the service's message when something could not be put back.</summary>
    public async Task<string?> RestoreAsync(FinderBaseline baseline, CancellationToken ct)
    {
        string? problem = null;
        try
        {
            ControlResponse use = await _control.SendAsync(new ControlRequest(ControlCommands.Use, Strategy: baseline.Strategy), ct);
            if (!use.Ok) problem = $"не удалось вернуть стратегию {baseline.Strategy}: {use.Error}";

            if (!baseline.Enabled)
            {
                ControlResponse off = await _control.SendAsync(new ControlRequest(ControlCommands.Off), ct);
                if (!off.Ok) problem ??= $"не удалось выключить обход DPI: {off.Error}";
            }
        }
        catch (ServiceUnavailableException ex)
        {
            problem = ex.Message;
        }
        return problem;
    }

    /// <summary>Makes <paramref name="strategyId"/> the choice (the bypass is already on). Returns the service's message when it refuses.</summary>
    public async Task<string?> ApplyAsync(string strategyId, CancellationToken ct)
    {
        try
        {
            ControlResponse use = await _control.SendAsync(new ControlRequest(ControlCommands.Use, Strategy: strategyId), ct);
            return use.Ok ? null : use.Error;
        }
        catch (ServiceUnavailableException ex)
        {
            return ex.Message;
        }
    }
}
