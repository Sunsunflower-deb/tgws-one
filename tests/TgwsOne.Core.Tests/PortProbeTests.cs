using System.Net;
using System.Net.Sockets;

namespace TgwsOne.Core.Tests;

public class PortProbeTests
{
    private static readonly IPAddress Loopback = IPAddress.Loopback;

    [Fact]
    public void IsBusy_FreePort_False()
        => Assert.False(PortProbe.IsBusy(Loopback, TestEnv.FreePort()));

    [Fact]
    public void IsBusy_ListeningLoopback_True()
    {
        using var l = new TcpListener(Loopback, 0);
        l.Start();

        Assert.True(PortProbe.IsBusy(Loopback, ((IPEndPoint)l.LocalEndpoint).Port));
    }

    [Fact]
    public void IsBusy_ListeningAny_TrueForLoopbackHost()
    {
        // On Windows a specific-address bind can succeed while another socket listens on 0.0.0.0;
        // the connect probe is what catches it.
        using var l = new TcpListener(IPAddress.Any, 0);
        l.Start();

        Assert.True(PortProbe.IsBusy(Loopback, ((IPEndPoint)l.LocalEndpoint).Port));
    }

    [Fact]
    public void IsBusy_AddressNotOnThisMachine_IsNotReportedAsBusy()
    {
        // A host the machine does not own can't be bound: that is a configuration problem the engine will
        // report itself, not a busy port to search around.
        Assert.False(PortProbe.IsBusy(IPAddress.Parse("203.0.113.77"), TestEnv.FreePort()));
    }

    [Fact]
    public void FindFree_SkipsBusy()
    {
        var held = TestEnv.BindConsecutive(2);
        try
        {
            int first = ((IPEndPoint)held[0].LocalEndpoint).Port;
            held[1].Stop();   // release the second port: it is the one expected back

            Assert.Equal(first + 1, PortProbe.FindFree(Loopback, first, 5));
        }
        finally { foreach (var l in held) l.Stop(); }
    }

    [Fact]
    public void FindFree_AllBusy_Null()
    {
        var held = TestEnv.BindConsecutive(3);
        try
        {
            int first = ((IPEndPoint)held[0].LocalEndpoint).Port;

            Assert.Null(PortProbe.FindFree(Loopback, first, count: 3));
        }
        finally { foreach (var l in held) l.Stop(); }
    }

    [Fact]
    public void FindFree_StaysWithinPortRange()
    {
        Assert.Null(PortProbe.FindFree(Loopback, 65536, 5));
        Assert.True(PortProbe.FindFree(Loopback, 65535, 50) is null or 65535);
    }
}
