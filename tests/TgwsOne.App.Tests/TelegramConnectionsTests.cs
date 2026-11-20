using System.Net.NetworkInformation;
using TgwsOne.Services;

namespace TgwsOne.Tests;

public class TelegramConnectionsTests
{
    private const int Port = 1443;

    [Fact]
    public void CountsEstablishedConnectionsToTheProxyPort()
    {
        var net = new FakeNetInfo();
        net.Set(Samples.Tcp(Port, 50001), Samples.Tcp(Port, 50002));

        Assert.Equal(2, TelegramConnections.Count(Port, net));
    }

    [Fact]
    public void IgnoresConnectionsThatAreNotEstablished()
    {
        var net = new FakeNetInfo();
        net.Set(
            Samples.Tcp(Port, 50001, TcpState.TimeWait),
            Samples.Tcp(Port, 50002, TcpState.SynReceived),
            Samples.Tcp(Port, 0, TcpState.Listen),
            Samples.Tcp(Port, 50003, TcpState.CloseWait),
            Samples.Tcp(Port, 50004));

        Assert.Equal(1, TelegramConnections.Count(Port, net));
    }

    [Fact]
    public void IgnoresConnectionsToOtherPorts_AndTheEnginesOwnOutgoingOnes()
    {
        var net = new FakeNetInfo();
        // The engine talking to Telegram: our port is the *remote* end of nothing here, the local port is an ephemeral one.
        net.Set(Samples.Tcp(40001, 443), Samples.Tcp(8080, 50001), Samples.Tcp(40002, Port));

        Assert.Equal(0, TelegramConnections.Count(Port, net));
    }

    [Fact]
    public void NoPort_IsNoConnections()
    {
        var net = new FakeNetInfo();
        net.Set(Samples.Tcp(0, 50001));

        Assert.Equal(0, TelegramConnections.Count(0, net));
    }

    [Fact]
    public void TheRealTable_CanBeRead()
    {
        // Smoke test of the system implementation: it must not throw, and nothing listens on port 1 here.
        Assert.Equal(0, TelegramConnections.Count(1, new SystemNetInfo()));
    }
}
