using System.Net;
using System.Net.Sockets;

namespace TgwsOne.Engine.Desync;

public sealed class SocketTricksTests
{
    private static bool IsLinux => OperatingSystem.IsLinux() || OperatingSystem.IsAndroid();

    [Fact]
    public void Factory_ReturnsThePlatformImplementation()
    {
        ISocketTricks tricks = SocketTricks.ForCurrentPlatform();

        if (IsLinux)
        {
            Assert.IsType<LinuxSocketTricks>(tricks);
            Assert.True(tricks.SupportsLowTtl);
            Assert.True(tricks.SupportsSpliceFake);
            Assert.True(tricks.SupportsUrgent);
        }
        else if (OperatingSystem.IsWindows())
        {
            Assert.IsType<WindowsSocketTricks>(tricks);
            Assert.True(tricks.SupportsLowTtl);
            Assert.False(tricks.SupportsSpliceFake);
            Assert.False(tricks.SupportsUrgent);
        }
        else
        {
            Assert.IsType<UnsupportedSocketTricks>(tricks);
            Assert.False(tricks.SupportsLowTtl);
            Assert.False(tricks.SupportsSpliceFake);
            Assert.False(tricks.SupportsUrgent);
        }
    }

    [Fact]
    public void Unsupported_ReportsNothing()
    {
        var tricks = new UnsupportedSocketTricks();
        using var socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);

        Assert.False(tricks.SupportsLowTtl);
        Assert.False(tricks.SupportsSpliceFake);
        Assert.False(tricks.SupportsUrgent);
        Assert.Equal(-1, tricks.GetSystemTtl(socket));
        tricks.SetTtl(socket, 5);
        Assert.False(tricks.TrySendSpliced(socket, [1, 2, 3]));
        Assert.False(tricks.TrySendUrgent(socket, [1]));
        Assert.Null(tricks.ReadRetransmitCount(socket));
    }

    [Fact]
    public void Windows_OnlySupportsTtl()
    {
        var tricks = new WindowsSocketTricks();
        using var socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);

        Assert.True(tricks.SupportsLowTtl);
        Assert.False(tricks.SupportsSpliceFake);
        Assert.False(tricks.SupportsUrgent);
        Assert.False(tricks.TrySendSpliced(socket, [1, 2, 3]));
        Assert.False(tricks.TrySendUrgent(socket, [1]));
        Assert.Null(tricks.ReadRetransmitCount(socket));
    }

    [Fact]
    public void PageAlignedBuffer_IsPageAligned()
    {
        using var buffer = new PageAlignedBuffer(100);
        buffer.Write([1, 2, 3]);

        Assert.Equal(100, buffer.Length);
        Assert.Equal(0, buffer.Pointer.ToInt64() % Environment.SystemPageSize);
    }

    [Fact]
    public void Ttl_RoundTrips_OnIpv4Loopback()
    {
        if (!IsLinux && !OperatingSystem.IsWindows()) return;

        ISocketTricks tricks = SocketTricks.ForCurrentPlatform();
        var (listener, client, server) = Connect(AddressFamily.InterNetwork);

        using (listener)
        using (client)
        using (server)
        {
            int systemTtl = tricks.GetSystemTtl(client);
            Assert.InRange(systemTtl, 1, 255);
            Assert.Equal(systemTtl, client.Ttl);

            tricks.SetTtl(client, 5);
            Assert.Equal(5, client.Ttl);
            Assert.Equal(5, tricks.GetSystemTtl(client));

            tricks.SetTtl(client, systemTtl);
            Assert.Equal(systemTtl, client.Ttl);
            Assert.Equal(systemTtl, tricks.GetSystemTtl(client));
        }
    }

    [Fact]
    public void Ttl_RoundTrips_OnIpv6Loopback_WhenAvailable()
    {
        if (!IsLinux) return;
        if (!TryConnect(AddressFamily.InterNetworkV6, out Socket? listener, out Socket? client, out Socket? server))
            return;

        var tricks = new LinuxSocketTricks();
        using (listener)
        using (client)
        using (server)
        {
            int systemHops = tricks.GetSystemTtl(client);
            Assert.InRange(systemHops, 1, 255);

            tricks.SetTtl(client, 5);
            Assert.Equal(5, tricks.GetSystemTtl(client));

            tricks.SetTtl(client, systemHops);
            Assert.Equal(systemHops, tricks.GetSystemTtl(client));
        }
    }

    [Fact]
    public void Ttl_ChangesBothOptions_OnDualModeSocketToIpv4Loopback()
    {
        if (!IsLinux) return;
        if (!Socket.OSSupportsIPv6) return;

        using var listener = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        listener.Bind(new IPEndPoint(IPAddress.Loopback, 0));
        listener.Listen(1);

        // EgressSocket creates the same kind of socket: the parameterless constructor picks a dual-mode IPv6 socket,
        // so a connection to the IPv4 loopback literal goes through a v4-mapped address on the wire.
        using var client = new Socket(SocketType.Stream, ProtocolType.Tcp);
        try
        {
            client.Connect(new IPEndPoint(IPAddress.Loopback, ((IPEndPoint)listener.LocalEndPoint!).Port));
        }
        catch (SocketException)
        {
            return; // an environment without a v4-mapped loopback has nothing to check here
        }

        using var server = listener.Accept();

        Assert.Equal(AddressFamily.InterNetworkV6, client.AddressFamily);
        Assert.True(client.DualMode);

        var tricks = new LinuxSocketTricks();
        int systemTtl = tricks.GetSystemTtl(client);
        Assert.InRange(systemTtl, 1, 255);
        Assert.InRange(tricks.GetTtlOption(client, ipv4: true), 1, 255);
        Assert.Equal(systemTtl, tricks.GetTtlOption(client, ipv4: false));

        tricks.SetTtl(client, 5);
        Assert.Equal(0, tricks.LastError);

        // The E2.6 regression: before the fix only IPV6_UNICAST_HOPS changed, so the IPv4 packet kept the system TTL.
        // The dual-mode socket must now carry the low value in both options; the IPv4 one is the one the v4-mapped
        // packet actually uses.
        Assert.Equal(5, tricks.GetTtlOption(client, ipv4: true));
        Assert.Equal(5, tricks.GetTtlOption(client, ipv4: false));
        Assert.Equal(5, client.Ttl);

        tricks.SetTtl(client, systemTtl);
        Assert.Equal(systemTtl, tricks.GetTtlOption(client, ipv4: true));
        Assert.Equal(systemTtl, tricks.GetTtlOption(client, ipv4: false));
    }

    [Fact]
    public void TrySendSpliced_DeliversAllBytes()
    {
        if (!IsLinux) return;

        var tricks = new LinuxSocketTricks();
        var (listener, client, server) = Connect(AddressFamily.InterNetwork);

        using (listener)
        using (client)
        using (server)
        {
            byte[] payload = new byte[100];
            for (int i = 0; i < payload.Length; i++) payload[i] = (byte)(i * 7 + 1);

            Assert.True(tricks.TrySendSpliced(client, payload));
            Assert.Equal(payload, ReadExactly(server, payload.Length));
        }
    }

    [Fact]
    public void TrySendUrgent_DeliversTheByte()
    {
        if (!IsLinux) return;

        var tricks = new LinuxSocketTricks();
        var (listener, client, server) = Connect(AddressFamily.InterNetwork);

        using (listener)
        using (client)
        using (server)
        {
            // Without SO_OOBINLINE the urgent byte is removed from the normal stream, so the receiver would see an
            // empty read even though the segment arrived.
            server.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.OutOfBandInline, true);

            Assert.True(tricks.TrySendUrgent(client, [0x2A]));
            Assert.Equal([0x2A], ReadExactly(server, 1));
        }
    }

    [Fact]
    public void ReadRetransmitCount_IsAvailableOnLinux()
    {
        if (!IsLinux) return;

        var tricks = new LinuxSocketTricks();
        var (listener, client, server) = Connect(AddressFamily.InterNetwork);

        using (listener)
        using (client)
        using (server)
        {
            long? retransmits = tricks.ReadRetransmitCount(client);
            Assert.NotNull(retransmits);
            Assert.True(retransmits >= 0);
        }
    }

    private static (Socket Listener, Socket Client, Socket Server) Connect(AddressFamily family)
    {
        if (!TryConnect(family, out Socket? listener, out Socket? client, out Socket? server))
            throw new InvalidOperationException($"{family} loopback is not available");
        return (listener, client, server);
    }

    private static bool TryConnect(
        AddressFamily family,
        out Socket listener,
        out Socket client,
        out Socket server)
    {
        listener = null!;
        client = null!;
        server = null!;

        IPAddress loopback = family == AddressFamily.InterNetworkV6 ? IPAddress.IPv6Loopback : IPAddress.Loopback;
        if (family == AddressFamily.InterNetworkV6 && !Socket.OSSupportsIPv6) return false;

        try
        {
            listener = new Socket(family, SocketType.Stream, ProtocolType.Tcp);
            listener.Bind(new IPEndPoint(loopback, 0));
            listener.Listen(1);

            client = new Socket(family, SocketType.Stream, ProtocolType.Tcp);
            client.NoDelay = true;
            client.Connect(listener.LocalEndPoint!);
            server = listener.Accept();
            server.NoDelay = true;
            return true;
        }
        catch (SocketException)
        {
            listener?.Dispose();
            client?.Dispose();
            server?.Dispose();
            listener = null!;
            client = null!;
            server = null!;
            return false;
        }
    }

    private static byte[] ReadExactly(Socket socket, int count)
    {
        socket.ReceiveTimeout = 5000;
        byte[] buffer = new byte[count];
        int read = 0;
        while (read < count)
        {
            int n = socket.Receive(buffer, read, count - read, SocketFlags.None);
            if (n == 0) throw new IOException("the peer closed before all bytes arrived");
            read += n;
        }

        return buffer;
    }
}
