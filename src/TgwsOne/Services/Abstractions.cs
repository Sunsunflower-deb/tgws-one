using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using TgwsOne.Core;
using TgwsOne.ViewModels;

namespace TgwsOne.Services;

/// <summary>What the UI needs from the proxy: start, stop, look. An adapter over <see cref="ProxyController"/> so view-models can be tested with a fake.</summary>
internal interface IProxyControl
{
    Task<StartResult> StartAsync(CancellationToken ct = default);
    Task<StopOutcome> StopAsync(CancellationToken ct = default);
    ProxyStatus GetStatus();
    bool RestartNeeded();
}

internal sealed class ProxyControl(IProxyEngineControl engine) : IProxyControl
{
    public Task<StartResult> StartAsync(CancellationToken ct = default) => engine.StartAsync(ct);
    public Task<StopOutcome> StopAsync(CancellationToken ct = default) => engine.StopAsync(ct);
    public ProxyStatus GetStatus() => engine.GetStatus();
    public bool RestartNeeded() => engine.RestartNeeded();
}

/// <summary>One row of the machine's TCP table.</summary>
internal readonly record struct TcpConnection(IPEndPoint Local, IPEndPoint Remote, TcpState State);

/// <summary>What kind of link a network interface is, as far as picking an address to give a phone is concerned.</summary>
internal enum NetKind { Ethernet, Wireless, Loopback, Tunnel, Other }

/// <summary>One network interface: just what choosing a LAN address needs.</summary>
internal sealed record NetInterfaceInfo(string Name, string Description, bool IsUp, NetKind Kind,
                                        IReadOnlyList<IPAddress> Addresses, IReadOnlyList<IPAddress> Gateways);

/// <summary>What the UI needs to know about the machine's network, so it can be faked.</summary>
internal interface INetInfo
{
    IReadOnlyList<TcpConnection> TcpConnections();

    /// <summary>The interfaces with their IPv4 addresses and IPv4 gateways.</summary>
    IReadOnlyList<NetInterfaceInfo> Interfaces();
}

internal sealed class SystemNetInfo : INetInfo
{
    public IReadOnlyList<TcpConnection> TcpConnections()
        => [.. IPGlobalProperties.GetIPGlobalProperties().GetActiveTcpConnections()
                .Select(c => new TcpConnection(c.LocalEndPoint, c.RemoteEndPoint, c.State))];

    public IReadOnlyList<NetInterfaceInfo> Interfaces()
    {
        var all = new List<NetInterfaceInfo>();
        foreach (NetworkInterface nic in NetworkInterface.GetAllNetworkInterfaces())
        {
            try
            {
                IPInterfaceProperties props = nic.GetIPProperties();
                all.Add(new NetInterfaceInfo(
                    nic.Name, nic.Description, nic.OperationalStatus == OperationalStatus.Up, KindOf(nic.NetworkInterfaceType),
                    [.. props.UnicastAddresses.Select(u => u.Address).Where(a => a.AddressFamily == AddressFamily.InterNetwork)],
                    [.. props.GatewayAddresses.Select(g => g.Address).Where(a => a.AddressFamily == AddressFamily.InterNetwork)]));
            }
            catch (Exception ex) when (ex is NetworkInformationException or PlatformNotSupportedException)
            {
                // an interface that vanished while being asked about: not a candidate for anything
            }
        }
        return all;
    }

    private static NetKind KindOf(NetworkInterfaceType type) => type switch
    {
        NetworkInterfaceType.Ethernet or NetworkInterfaceType.GigabitEthernet or NetworkInterfaceType.FastEthernetT => NetKind.Ethernet,
        NetworkInterfaceType.Wireless80211 => NetKind.Wireless,
        NetworkInterfaceType.Loopback => NetKind.Loopback,
        NetworkInterfaceType.Tunnel or NetworkInterfaceType.Ppp => NetKind.Tunnel,
        _ => NetKind.Other,
    };
}

/// <summary>Something that happens every so often, on the UI thread: a seam so that tests can make time pass by hand.</summary>
internal interface ITicker
{
    /// <returns>Disposing it stops the ticks.</returns>
    IDisposable Start(TimeSpan period, Action tick);
}

/// <summary>Time, so throttles and timeouts can be tested without waiting.</summary>
internal interface IClock
{
    DateTimeOffset Now { get; }
}

internal sealed class SystemClock : IClock
{
    public DateTimeOffset Now => DateTimeOffset.Now;
}

/// <summary>The user's settings file as the pages need it. Every call reads the file afresh: a start that had to switch ports rewrites it behind our back.</summary>
internal interface IAppSettings
{
    /// <summary>The proxy settings as they are on disk now (defaults where a value is missing or wrong); never throws.</summary>
    ProxySettings Load();

    /// <summary>The <c>tg://proxy</c> link for Telegram on this machine; makes the secret first if there is none yet.</summary>
    string TelegramLink();

    /// <summary>Replaces the secret with a new random one and saves it. The running proxy keeps the old one until it is restarted.</summary>
    void ReplaceSecret();

    /// <summary>The settings as the form shows them (strings, with the defaults where a value is missing).</summary>
    ProxyForm ReadForm();

    /// <summary>The secret; one is made first if there is none yet.</summary>
    string Secret();

    /// <summary>The first-run wizard has been through (or dismissed): it is never shown again.</summary>
    bool FirstRunDone { get; }

    /// <summary>Writes <c>[app] firstrun=done</c>.</summary>
    void MarkFirstRunDone();

    /// <summary>The first close to the tray has been explained once («TgwsOne работает в трее»): never again.</summary>
    bool TrayHintShown { get; }

    void MarkTrayHintShown();

    /// <summary>Sharing with a phone on this network is on: the proxy listens on every interface.</summary>
    bool LanSharing { get; }

    /// <summary>The address chosen for the phone's link, if one was.</summary>
    string? LanAddress { get; }

    /// <summary>What the listen host goes back to when sharing is turned off.</summary>
    string PreviousHost { get; }

    /// <summary>
    /// Writes the validated settings, and the sharing switch with the address chosen for it. Turning sharing on makes the
    /// proxy listen on every interface and remembers the host it used before; turning it off puts that host back.
    /// </summary>
    /// <param name="previousHost">With sharing on: the host to go back to when it is turned off (what the form had); null keeps what the file has.</param>
    void Save(ProxySettings settings, bool lanSharing, string? lanAddress, string? previousHost = null);
}

/// <summary>Opens a URL in whatever the system uses for it (Telegram for <c>tg://</c>).</summary>
internal interface ILauncher
{
    /// <returns>False when nothing could be started for the URL.</returns>
    Task<bool> OpenAsync(string url);
}

internal interface IClipboardService
{
    Task SetTextAsync(string text);
}

internal interface IDialogService
{
    /// <returns>True when the user chose <paramref name="accept"/>.</returns>
    Task<bool> ConfirmAsync(string title, string message, string accept, string cancel);

    /// <summary>Shows the strategy finder over the main window; returns when its window is closed.</summary>
    Task ShowFinderAsync(FinderViewModel finder);

    /// <summary>Shows the code for a phone over the main window; returns when its window is closed.</summary>
    Task ShowQrAsync(QrViewModel qr);

    /// <summary>Shows the editor of a custom strategy over the main window; returns when its window is closed.</summary>
    Task ShowCustomStrategyAsync(CustomStrategyViewModel editor);
}
