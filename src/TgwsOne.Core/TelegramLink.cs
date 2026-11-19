using System.Net;

namespace TgwsOne.Core;

public static class TelegramLink
{
    /// <summary>The address a client connects to for a given listen address: wildcards become loopback.</summary>
    public static IPAddress ConnectAddress(IPAddress listen)
    {
        if (listen.Equals(IPAddress.Any)) return IPAddress.Loopback;
        if (listen.Equals(IPAddress.IPv6Any)) return IPAddress.IPv6Loopback;
        return listen;
    }

    /// <summary>
    /// <c>tg://proxy?server=…&amp;port=…&amp;secret=dd…</c>. <c>server</c> is a query parameter, so an IPv6
    /// address goes in URL-encoded and without brackets.
    /// </summary>
    public static string Build(IPAddress host, int port, string secret)
        => $"tg://proxy?server={Uri.EscapeDataString(ConnectAddress(host).ToString())}&port={port}&secret=dd{secret}";
}
