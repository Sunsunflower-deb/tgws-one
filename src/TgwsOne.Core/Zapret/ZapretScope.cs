namespace TgwsOne.Core.Zapret;

/// <summary>
/// What the bypass applies to.
/// <para>
/// <see cref="Telegram"/> — only TLS on 443 to the Telegram hosts: that is the proxy's own traffic (and Telegram Desktop
/// talking to the data centres directly, if it is not using the proxy).
/// </para>
/// <para>
/// <see cref="All"/> — the same plus every TLS connection to Cloudflare's address ranges, i.e. other applications too
/// (browsers first of all). Opt-in: it is the wider net, and it changes how unrelated programs behave.
/// </para>
/// </summary>
public enum ZapretScope
{
    /// <summary>Only Telegram's TLS: the proxy's own traffic.</summary>
    Telegram,

    /// <summary>Every application: Telegram's hosts plus all Cloudflare ranges.</summary>
    All,
}

/// <summary>The wire and ini spelling of <see cref="ZapretScope"/>: <c>telegram</c> / <c>all</c>.</summary>
public static class ZapretScopes
{
    public const string Telegram = "telegram";
    public const string All = "all";

    /// <summary>Anything but <c>all</c> means the narrow, default scope (an unknown value must not widen the bypass).</summary>
    public static ZapretScope Parse(string? value)
        => string.Equals(value?.Trim(), All, StringComparison.OrdinalIgnoreCase) ? ZapretScope.All : ZapretScope.Telegram;

    public static string ToWire(this ZapretScope scope) => scope == ZapretScope.All ? All : Telegram;
}
