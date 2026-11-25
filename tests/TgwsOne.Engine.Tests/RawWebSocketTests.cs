using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using TgwsOne.Engine.Transport;

namespace TgwsOne.Engine.Tests;

public class RawWebSocketTests
{
    [Fact]
    public async Task HandshakeSendReceive_AndClose()
    {
        using var server = new FakeWsServer(async (stream, _) =>
        {
            await ReadHeadersAsync(stream);
            await stream.WriteAsync(Encoding.ASCII.GetBytes(
                "HTTP/1.1 101 Switching Protocols\r\nUpgrade: websocket\r\nConnection: Upgrade\r\n\r\n"));
            await stream.FlushAsync();
            while (true)
            {
                (int opcode, byte[] payload) = await ReadFrameAsync(stream);
                if (opcode == 0x8) return;
                await WriteFrameAsync(stream, 0x2, payload);
            }
        });

        await using RawWebSocket ws = await RawWebSocket.ConnectAsync(
            "127.0.0.1", "example.test", TimeSpan.FromSeconds(5), secure: false, port: server.Port);

        byte[] message = RandomNumberGenerator.GetBytes(5000);
        await ws.SendAsync(message);
        Assert.Equal(message, await ws.ReceiveAsync());

        await ws.CloseAsync();
        Assert.True(ws.IsClosed);
    }

    [Fact]
    public async Task HandshakeThatIsNot101_ThrowsWithStatusAndLocation()
    {
        using var server = new FakeWsServer(async (stream, _) =>
        {
            await ReadHeadersAsync(stream);
            await stream.WriteAsync(Encoding.ASCII.GetBytes(
                "HTTP/1.1 302 Found\r\nLocation: https://example.test/\r\nContent-Length: 0\r\n\r\n"));
            await stream.FlushAsync();
        });

        var ex = await Assert.ThrowsAsync<WsHandshakeException>(() => RawWebSocket.ConnectAsync(
            "127.0.0.1", "example.test", TimeSpan.FromSeconds(5), secure: false, port: server.Port));

        Assert.Equal(302, ex.StatusCode);
        Assert.True(ex.IsRedirect);
        Assert.Equal("https://example.test/", ex.Location);
    }

    [Fact]
    public async Task Ping_IsAnswered_AndDoesNotSurfaceAsAMessage()
    {
        using var server = new FakeWsServer(async (stream, _) =>
        {
            await ReadHeadersAsync(stream);
            await stream.WriteAsync(Encoding.ASCII.GetBytes(
                "HTTP/1.1 101 Switching Protocols\r\nUpgrade: websocket\r\nConnection: Upgrade\r\n\r\n"));
            await stream.FlushAsync();
            await WriteFrameAsync(stream, 0x9, "ping"u8.ToArray());
            await WriteFrameAsync(stream, 0x2, "hello"u8.ToArray());
            while (true)
            {
                (int opcode, byte[] payload) = await ReadFrameAsync(stream);
                if (opcode == 0x8) return;
                if (opcode == 0xA && Encoding.ASCII.GetString(payload) == "ping") return;
            }
        });

        await using RawWebSocket ws = await RawWebSocket.ConnectAsync(
            "127.0.0.1", "example.test", TimeSpan.FromSeconds(5), secure: false, port: server.Port);

        Assert.Equal("hello"u8.ToArray(), await ws.ReceiveAsync());
    }

    [Fact]
    public async Task FragmentedMessage_IsAssembled()
    {
        using var server = new FakeWsServer(async (stream, _) =>
        {
            await ReadHeadersAsync(stream);
            await stream.WriteAsync(Encoding.ASCII.GetBytes(
                "HTTP/1.1 101 Switching Protocols\r\nUpgrade: websocket\r\nConnection: Upgrade\r\n\r\n"));
            await stream.FlushAsync();
            await WriteFrameAsync(stream, 0x2, "hel"u8.ToArray(), fin: false);
            await WriteFrameAsync(stream, 0x0, "lo"u8.ToArray(), fin: true);
            while (true)
            {
                var frame = await ReadFrameAsync(stream);
                if (frame.Opcode == 0x8) return;
            }
        });

        await using RawWebSocket ws = await RawWebSocket.ConnectAsync(
            "127.0.0.1", "example.test", TimeSpan.FromSeconds(5), secure: false, port: server.Port);

        Assert.Equal("hello"u8.ToArray(), await ws.ReceiveAsync());
    }

    // ---------- a minimal RFC 6455 peer for the tests ----------

    private sealed class FakeWsServer : IDisposable
    {
        private readonly TcpListener _listener;
        private readonly Task _accepting;
        private readonly CancellationTokenSource _stop = new();

        public int Port { get; }

        public FakeWsServer(Func<NetworkStream, CancellationToken, Task> handler)
        {
            _listener = new TcpListener(IPAddress.Loopback, 0);
            _listener.Start();
            Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
            _accepting = Task.Run(async () =>
            {
                try
                {
                    using TcpClient client = await _listener.AcceptTcpClientAsync(_stop.Token);
                    await handler(client.GetStream(), _stop.Token);
                }
                catch (Exception ex) when (ex is OperationCanceledException or SocketException or IOException)
                {
                    // the test finished or the client went away
                }
            });
        }

        public void Dispose()
        {
            _stop.Cancel();
            _listener.Stop();
            try { _accepting.Wait(TimeSpan.FromSeconds(2)); } catch (AggregateException) { }
            _stop.Dispose();
        }
    }

    private static async Task ReadHeadersAsync(Stream stream)
    {
        var bytes = new List<byte>();
        var one = new byte[1];
        while (bytes.Count < 16 * 1024)
        {
            int read = await stream.ReadAsync(one);
            if (read == 0) return;
            bytes.Add(one[0]);
            if (bytes.Count >= 4 && bytes[^4] == '\r' && bytes[^3] == '\n' && bytes[^2] == '\r' && bytes[^1] == '\n')
                return;
        }
    }

    private static async Task<(int Opcode, byte[] Payload)> ReadFrameAsync(Stream stream)
    {
        byte[] header = await ReadExactlyAsync(stream, 2);
        int opcode = header[0] & 0x0F;
        long length = header[1] & 0x7F;
        if (length == 126) length = BinaryPrimitives.ReadUInt16BigEndian(await ReadExactlyAsync(stream, 2));
        else if (length == 127) length = (long)BinaryPrimitives.ReadUInt64BigEndian(await ReadExactlyAsync(stream, 8));
        byte[] maskKey = (header[1] & 0x80) != 0 ? await ReadExactlyAsync(stream, 4) : [];
        byte[] payload = await ReadExactlyAsync(stream, (int)length);
        if (maskKey.Length == 4)
        {
            for (int i = 0; i < payload.Length; i++) payload[i] ^= maskKey[i & 3];
        }
        return (opcode, payload);
    }

    private static async Task WriteFrameAsync(Stream stream, int opcode, byte[] payload, bool fin = true)
    {
        int headerLength = payload.Length < 126 ? 2 : payload.Length < 65536 ? 4 : 10;
        var frame = new byte[headerLength + payload.Length];
        frame[0] = (byte)((fin ? 0x80 : 0) | opcode);
        if (payload.Length < 126)
        {
            frame[1] = (byte)payload.Length;
        }
        else if (payload.Length < 65536)
        {
            frame[1] = 126;
            BinaryPrimitives.WriteUInt16BigEndian(frame.AsSpan(2), (ushort)payload.Length);
        }
        else
        {
            frame[1] = 127;
            BinaryPrimitives.WriteUInt64BigEndian(frame.AsSpan(2), (ulong)payload.Length);
        }
        payload.CopyTo(frame, headerLength);
        await stream.WriteAsync(frame);
        await stream.FlushAsync();
    }

    private static async Task<byte[]> ReadExactlyAsync(Stream stream, int count)
    {
        var buffer = new byte[count];
        await stream.ReadExactlyAsync(buffer);
        return buffer;
    }
}
