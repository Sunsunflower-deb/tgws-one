using TgwsOne.Engine.Desync;

namespace TgwsOne.Mobile;

/// <summary>How one candidate did: Telegram handshakes that got through (of five), the CF-proxy handshake, the end-to-end check, the median handshake time, or why it could not be tried.</summary>
public sealed record FinderRow(string StrategyId, int TelegramOk, bool CloudflareOk, bool SelfCheckOk, TimeSpan? Median, string? Error);

/// <summary>What the bypass was before the run: this is what «Вернуть как было» puts back.</summary>
public sealed record FinderBaseline(string Strategy, bool Enabled);

/// <summary>The finder could not even start; the message is for the user.</summary>
public sealed class FinderException(string message) : Exception(message);

/// <summary>
/// The live bypass as the finder and diagnostics need it. Applying a candidate selects it in the engine's
/// <see cref="IDesyncController"/>; the engine drops its idle pools on a selection change, so the next measurement
/// opens fresh connections with the new strategy.
/// </summary>
public interface IDesyncControl
{
    /// <summary>The current operator's rotation, in order.</summary>
    IReadOnlyList<DesyncStrategy> Rotation { get; }

    /// <summary>What <c>Авто</c> has chosen for each host so far; empty unless it is the selection.</summary>
    IReadOnlyList<AutoHost> AutoHosts { get; }

    /// <summary>The strategy and whether the bypass is on, as they are now.</summary>
    Task<FinderBaseline> CaptureAsync(CancellationToken ct = default);

    /// <summary>Selects a finder candidate and drops the idle pools. Returns the engine's message when it refuses.</summary>
    Task<string?> ApplyAsync(string candidateId, CancellationToken ct = default);

    /// <summary>Puts the bypass back as it was. Returns the message when something could not be put back.</summary>
    Task<string?> RestoreAsync(FinderBaseline baseline, CancellationToken ct = default);

    /// <summary>The strategy that would be used for an outgoing route right now; null means no desync.</summary>
    DesyncStrategy? Resolve(string sni, string route);
}

/// <summary>
/// Every network measurement the finder and diagnostics make: direct TCP/TLS, TLS through the bypass, the
/// 64 KiB Cloudflare download and the engine's own end-to-end exchange.
/// </summary>
public interface IEgressProbes
{
    /// <summary>The base domain the CF-proxy route is reached behind; the finder checks a kws host under it.</summary>
    string CloudflareDomain { get; }

    Task<bool> TcpAsync(string host, int port, TimeSpan timeout, CancellationToken ct);

    Task<TimeSpan?> TlsAsync(string host, int port, TimeSpan timeout, CancellationToken ct);

    /// <summary>A TLS handshake opened through the bypass layer, as the engine would open it for the route.</summary>
    Task<TimeSpan?> BypassHandshakeAsync(string host, int port, string route, TimeSpan timeout, CancellationToken ct);

    Task<(bool Ok, TimeSpan Elapsed)> DownloadAsync(Uri url, long expectedBytes, TimeSpan timeout, CancellationToken ct);

    /// <summary>The engine's full client-to-Telegram exchange; true only when a complete reply came back.</summary>
    Task<bool> SelfCheckAsync(TimeSpan timeout, CancellationToken ct);
}
