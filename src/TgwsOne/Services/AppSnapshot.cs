using TgwsOne.Core;
using TgwsOne.Core.Zapret;

namespace TgwsOne.Services;

/// <summary>The colour a status is shown in (headline dot, bypass line, tray icon).</summary>
internal enum Tone { Green, Amber, Grey, Red }

/// <summary>
/// Everything the UI shows about the two modules at one moment: the proxy (its state, the last start result, how many
/// Telegram connections it holds) and the bypass service. Immutable; a new one is published every poll.
/// </summary>
/// <param name="Proxy">What <see cref="IProxyControl.GetStatus"/> said.</param>
/// <param name="LastStart">The result of the last start made from this app; null until there was one.</param>
/// <param name="TelegramConnections">Established TCP connections to the proxy port: Telegram is connected when this is above zero.</param>
/// <param name="Bypass">The service's answer to <c>status</c>; null when it is unreachable.</param>
/// <param name="ServiceReachable">False when nobody listens on the control channel: the bypass is not installed or not running.</param>
internal sealed record AppSnapshot(ProxyStatus Proxy, StartResult? LastStart, int TelegramConnections, ZapretStatus? Bypass, bool ServiceReachable)
{
    /// <summary>What there is before the first poll has finished: nothing running, nothing known.</summary>
    public static AppSnapshot Empty { get; } = new(new ProxyStatus(Running: false, Listening: false, Port: 0, PortBusyByOther: false, Pid: null), null, 0, null, false);
}
