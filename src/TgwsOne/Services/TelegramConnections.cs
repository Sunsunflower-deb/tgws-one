using System.Net.NetworkInformation;

namespace TgwsOne.Services;

/// <summary>How many clients are talking to the proxy right now: Telegram is "connected" when this is above zero.</summary>
internal static class TelegramConnections
{
    /// <summary>
    /// Established TCP connections whose <em>local</em> end is the proxy's port, from loopback or the LAN alike. The
    /// engine's own connections to Telegram go the other way (the local port is an ephemeral one), so they don't count.
    /// </summary>
    public static int Count(int port, INetInfo net)
        => port <= 0 ? 0 : net.TcpConnections().Count(c => c.State == TcpState.Established && c.Local.Port == port);
}
