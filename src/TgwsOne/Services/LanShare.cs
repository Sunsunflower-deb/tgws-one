using System.Net;
using TgwsOne.Core;

namespace TgwsOne.Services;

/// <summary>An address a phone on the same network could reach this machine at, and which interface it belongs to.</summary>
internal sealed record LanAddress(string InterfaceName, IPAddress Address)
{
    public override string ToString() => $"{Address} ({InterfaceName})";
}

/// <summary>
/// Sharing the proxy with a phone on the same network: which address to give it, and the switch that makes the proxy listen on
/// every interface (remembering the host it listened on before, to put back).
/// </summary>
internal static class LanShare
{
    public const string WildcardHost = "0.0.0.0";
    public const string DefaultHost = "127.0.0.1";
    public const string PreviousHostKey = "lanhost_prev";
    public const string AddressKey = "lanaddr";

    // Names of the interfaces that carry no phone: containers, virtual machines, tunnels and VPNs. Names differ per OS, so this
    // is a heuristic on both the name and the description; a real network card has neither.
    private static readonly string[] VirtualMarks =
    [
        "docker", "veth", "br-", "virbr", "vmnet", "vboxnet", "vethernet", "hyper-v", "virtualbox", "vmware", "utun",
        "tun", "tap", "wg", "wireguard", "tailscale", "mullvad", "zt", "zerotier", "netbird", "nordlynx", "openvpn",
        "tap-windows", "wintun", "virtual",
    ];

    /// <summary>
    /// The IPv4 addresses of interfaces that are up, have an IPv4 default gateway (a route to the rest of the network, which is
    /// what a phone on the same Wi-Fi needs) and are not loopback, tunnel, virtual or link-local.
    /// </summary>
    public static IReadOnlyList<LanAddress> Candidates(INetInfo net)
    {
        var found = new List<LanAddress>();
        foreach (NetInterfaceInfo nic in net.Interfaces())
        {
            if (!nic.IsUp || nic.Kind is NetKind.Loopback or NetKind.Tunnel) continue;
            if (!nic.Gateways.Any(g => !g.Equals(IPAddress.Any))) continue;
            if (IsVirtual(nic)) continue;

            foreach (IPAddress address in nic.Addresses)
                if (!IPAddress.IsLoopback(address) && !IsLinkLocal(address))
                    found.Add(new LanAddress(nic.Name, address));
        }
        return found;
    }

    // A short mark (tun, tap, wg, zt) only counts at the start of a name: inside a longer word, or in a card's description, it would
    // be an accident ("Realtek PCIe GbE Family Controller" is not a tunnel).
    private static bool IsVirtual(NetInterfaceInfo nic)
        => VirtualMarks.Any(mark => nic.Name.StartsWith(mark, StringComparison.OrdinalIgnoreCase)
                                    || (mark.Length > 3 && (nic.Name.Contains(mark, StringComparison.OrdinalIgnoreCase)
                                                            || nic.Description.Contains(mark, StringComparison.OrdinalIgnoreCase))));

    private static bool IsLinkLocal(IPAddress a)
    {
        byte[] b = a.GetAddressBytes();
        return b.Length == 4 && b[0] == 169 && b[1] == 254;
    }

    /// <summary>True when the proxy listens on every interface.</summary>
    public static bool IsEnabled(IniFile ini) => ini.Get("proxy", "host") == WildcardHost;

    /// <summary>What <see cref="Disable"/> puts back: the host remembered when sharing was turned on, else the usual one.</summary>
    public static string PreviousHost(IniFile ini) => ini.Get("app", PreviousHostKey) is { Length: > 0 } prev ? prev : DefaultHost;

    /// <summary>Makes the proxy listen on every interface, remembering the host it had. Does not save the file.</summary>
    public static void Enable(IniFile ini)
    {
        string current = ini.Get("proxy", "host") is { Length: > 0 } h ? h : DefaultHost;
        if (current != WildcardHost) ini.Set("app", PreviousHostKey, current);   // already shared: keep what was remembered
        ini.Set("proxy", "host", WildcardHost);
    }

    /// <summary>Puts the remembered host back (if the proxy listens on every interface now) and forgets it. Does not save the file.</summary>
    public static void Disable(IniFile ini)
    {
        if (IsEnabled(ini)) ini.Set("proxy", "host", PreviousHost(ini));
        ini.Set("app", PreviousHostKey, "");
    }
}
