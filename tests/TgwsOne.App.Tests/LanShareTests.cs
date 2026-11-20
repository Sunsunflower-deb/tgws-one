using System.Net;
using TgwsOne.Core;
using TgwsOne.Services;

namespace TgwsOne.Tests;

public class LanShareTests
{
    private static NetInterfaceInfo Nic(string name, NetKind kind = NetKind.Ethernet, bool up = true, string address = "192.168.18.20",
                                        string? gateway = "192.168.18.1", string description = "Network adapter")
        => new(name, description, up, kind, [IPAddress.Parse(address)], gateway is null ? [] : [IPAddress.Parse(gateway)]);

    private static string[] Candidates(params NetInterfaceInfo[] interfaces)
    {
        var net = new FakeNetInfo();
        net.SetInterfaces(interfaces);
        return [.. LanShare.Candidates(net).Select(c => $"{c.InterfaceName} {c.Address}")];
    }

    // ----- which addresses a phone could use -----

    [Fact]
    public void ARealCardWithAGateway_IsACandidate()
    {
        Assert.Equal(["wlp2s0 192.168.18.20"], Candidates(Nic("wlp2s0", NetKind.Wireless)));
        Assert.Equal(["enp5s0 192.168.1.5"], Candidates(Nic("enp5s0", address: "192.168.1.5", gateway: "192.168.1.1")));
        Assert.Equal(["Wi-Fi 10.0.0.7"], Candidates(Nic("Wi-Fi", NetKind.Wireless, address: "10.0.0.7", gateway: "10.0.0.1", description: "Intel(R) Wi-Fi 6 AX201 160MHz")));
    }

    [Fact]
    public void ThisMachinesInterfaces_GiveTheLanAddressAndNothingElse()
    {
        // enp5s0 is the real network; the rest is what a machine with a VPN, containers and a VM carries.
        string[] found = Candidates(
            Nic("lo", NetKind.Loopback, address: "127.0.0.1", gateway: null),
            Nic("enp5s0"),
            Nic("tailscale0", NetKind.Tunnel, address: "100.64.0.5", gateway: "100.64.0.1"),
            Nic("wg0-mullvad", NetKind.Tunnel, address: "10.64.0.2", gateway: "10.64.0.1"),
            Nic("docker0", address: "172.17.0.1", gateway: "172.17.0.2"),
            Nic("veth1234", address: "172.18.0.1", gateway: "172.18.0.2"),
            Nic("br-9f3a2c", address: "172.19.0.1", gateway: "172.19.0.2"),
            Nic("virbr0", address: "192.168.122.1", gateway: "192.168.122.2"));

        Assert.Equal(["enp5s0 192.168.18.20"], found);
    }

    [Theory]
    [InlineData("docker0")]
    [InlineData("veth0a1b")]
    [InlineData("br-1234abcd")]
    [InlineData("virbr0")]
    [InlineData("vmnet8")]
    [InlineData("vboxnet0")]
    [InlineData("tun0")]
    [InlineData("tap0")]
    [InlineData("wg0")]
    [InlineData("tailscale0")]
    [InlineData("Tailscale")]
    [InlineData("mullvad-wg")]
    [InlineData("ztabcdef")]
    [InlineData("utun3")]
    [InlineData("vEthernet (Default Switch)")]
    [InlineData("nordlynx")]
    public void VirtualAndTunnelNames_AreNeverCandidates_EvenWithAGateway(string name)
    {
        Assert.Empty(Candidates(Nic(name)));
    }

    [Theory]
    [InlineData("TAP-Windows Adapter V9")]
    [InlineData("WireGuard Tunnel")]
    [InlineData("Microsoft Wi-Fi Direct Virtual Adapter")]
    [InlineData("VMware Virtual Ethernet Adapter for VMnet8")]
    [InlineData("VirtualBox Host-Only Ethernet Adapter")]
    [InlineData("Hyper-V Virtual Ethernet Adapter")]
    [InlineData("Wintun Userspace Tunnel")]
    public void VirtualDescriptions_AreNeverCandidates_WhateverTheNameSays(string description)
    {
        Assert.Empty(Candidates(Nic("Ethernet 3", description: description)));
    }

    [Theory]
    [InlineData("Realtek PCIe GbE Family Controller")]
    [InlineData("Intel(R) Ethernet Connection (7) I219-V")]
    [InlineData("Qualcomm Atheros QCA9377 802.11ac Wireless Adapter")]
    [InlineData("Broadcom NetXtreme Gigabit Ethernet")]
    public void RealHardware_IsNotMistakenForAVirtualOneByAShortMark(string description)
    {
        Assert.Single(Candidates(Nic("Ethernet", description: description)));
    }

    [Fact]
    public void AnInterfaceThatIsDown_OrHasNoGateway_OrAGatewayOfZeros_IsNotACandidate()
    {
        Assert.Empty(Candidates(Nic("enp5s0", up: false)));
        Assert.Empty(Candidates(Nic("enp5s0", gateway: null)));   // a network with no way out: nowhere for a phone to get to
        Assert.Empty(Candidates(Nic("enp5s0", gateway: "0.0.0.0")));
    }

    [Fact]
    public void ALoopbackOrLinkLocalAddress_IsNotACandidate()
    {
        Assert.Empty(Candidates(Nic("lo", NetKind.Loopback, address: "127.0.0.1")));
        Assert.Empty(Candidates(Nic("enp5s0", address: "127.0.1.1")));
        Assert.Empty(Candidates(Nic("enp5s0", address: "169.254.10.20")));   // no DHCP answered: nothing a phone could reach
    }

    [Fact]
    public void SeveralRealInterfaces_AreAllListed_InOrder()
    {
        string[] found = Candidates(
            Nic("enp5s0", address: "192.168.18.20"),
            Nic("wlp2s0", NetKind.Wireless, address: "10.1.1.9", gateway: "10.1.1.1"));

        Assert.Equal(["enp5s0 192.168.18.20", "wlp2s0 10.1.1.9"], found);
    }

    [Fact]
    public void AnInterfaceWithTwoAddresses_GivesBoth()
    {
        var nic = new NetInterfaceInfo("enp5s0", "Network adapter", true, NetKind.Ethernet,
            [IPAddress.Parse("192.168.18.20"), IPAddress.Parse("192.168.18.21")], [IPAddress.Parse("192.168.18.1")]);

        Assert.Equal(["enp5s0 192.168.18.20", "enp5s0 192.168.18.21"], Candidates(nic));
    }

    [Fact]
    public void TheAddressIsShownWithItsInterface()
    {
        Assert.Equal("192.168.18.20 (enp5s0)", new LanAddress("enp5s0", IPAddress.Parse("192.168.18.20")).ToString());
    }

    [Fact]
    public void TheRealMachine_CanBeAskedAboutItsInterfaces()
    {
        // Smoke test of the system implementation: it must not throw, and a loopback interface is always there.
        IReadOnlyList<NetInterfaceInfo> all = new SystemNetInfo().Interfaces();

        Assert.Contains(all, n => n.Kind == NetKind.Loopback);
        Assert.All(LanShare.Candidates(new SystemNetInfo()), c => Assert.False(IPAddress.IsLoopback(c.Address)));
    }

    // ----- the host switch -----

    private static IniFile Ini(TempDir tmp, string? host = null)
    {
        IniFile ini = IniFile.Load(tmp.Combine("tgwsone.ini"));
        if (host != null) ini.Set("proxy", "host", host);
        return ini;
    }

    [Fact]
    public void Enable_MakesTheProxyListenOnEveryInterface_AndRemembersTheHost()
    {
        using var tmp = new TempDir();
        IniFile ini = Ini(tmp, "127.0.0.2");

        LanShare.Enable(ini);

        Assert.Equal("0.0.0.0", ini.Get("proxy", "host"));
        Assert.Equal("127.0.0.2", ini.Get("app", "lanhost_prev"));
        Assert.True(LanShare.IsEnabled(ini));
    }

    [Fact]
    public void Disable_PutsTheRememberedHostBack_AndForgetsIt()
    {
        using var tmp = new TempDir();
        IniFile ini = Ini(tmp, "127.0.0.2");
        LanShare.Enable(ini);

        LanShare.Disable(ini);

        Assert.Equal("127.0.0.2", ini.Get("proxy", "host"));
        Assert.False(LanShare.IsEnabled(ini));
        Assert.Equal("127.0.0.1", LanShare.PreviousHost(ini));   // nothing remembered any more: the usual one
    }

    [Fact]
    public void WithNoHostSaidAnywhere_TheUsualOneIsRememberedAndPutBack()
    {
        using var tmp = new TempDir();
        IniFile ini = Ini(tmp);

        LanShare.Enable(ini);
        Assert.Equal("127.0.0.1", ini.Get("app", "lanhost_prev"));

        LanShare.Disable(ini);
        Assert.Equal("127.0.0.1", ini.Get("proxy", "host"));
    }

    [Fact]
    public void EnablingTwice_KeepsTheFirstHostNotTheWildcard()
    {
        using var tmp = new TempDir();
        IniFile ini = Ini(tmp, "127.0.0.2");
        LanShare.Enable(ini);

        LanShare.Enable(ini);   // already on: must not remember "0.0.0.0" as the host to go back to

        LanShare.Disable(ini);
        Assert.Equal("127.0.0.2", ini.Get("proxy", "host"));
    }

    [Fact]
    public void Disabling_WhenItWasNeverEnabled_LeavesACustomHostAlone()
    {
        using var tmp = new TempDir();
        IniFile ini = Ini(tmp, "127.0.0.3");

        LanShare.Disable(ini);

        Assert.Equal("127.0.0.3", ini.Get("proxy", "host"));
    }

    [Fact]
    public void TheRoundTrip_SurvivesTheFile()
    {
        using var tmp = new TempDir();
        IniFile first = Ini(tmp, "127.0.0.2");
        LanShare.Enable(first);
        first.Save();

        IniFile second = IniFile.Load(tmp.Combine("tgwsone.ini"));
        Assert.True(LanShare.IsEnabled(second));
        Assert.Equal("127.0.0.2", LanShare.PreviousHost(second));
        LanShare.Disable(second);
        second.Save();

        Assert.Equal("127.0.0.2", IniFile.Load(tmp.Combine("tgwsone.ini")).Get("proxy", "host"));
    }
}
