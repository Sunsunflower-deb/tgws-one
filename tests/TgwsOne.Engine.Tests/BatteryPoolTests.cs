using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using TgwsOne.Engine.Transport;

namespace TgwsOne.Engine.Tests;

/// <summary>
/// The battery side of the pools: a proxy that had no client for a while must let go of its warm sockets, and the
/// next client must start the warm-up again. The clock is the pool's <see cref="TimeProvider"/>, so the test moves
/// it instead of waiting for the real one.
/// </summary>
public sealed class BatteryPoolTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(5);

    [Fact]
    public async Task WebSocketPool_IdleLongerThanTheSetting_DropsFreeConnections()
    {
        var time = new ManualTime();
        using var server = new PoolWsServer();
        await using var pool = NewWsPool(time, idleSeconds: 30, server);

        await TakeAndReleaseAsync(pool);
        Assert.True(await Ports.WaitForAsync(() => pool.IdleCount >= 1, Timeout));
        Assert.True(server.Live >= 1);

        time.Advance(TimeSpan.FromSeconds(31));

        Assert.True(pool.CloseIfIdle());
        Assert.Equal(0, pool.IdleCount);
        Assert.True(await Ports.WaitForAsync(() => server.Live == 0, Timeout));
    }

    [Fact]
    public async Task WebSocketPool_GetAsyncAfterTheSleep_StartsTheRefillAgain()
    {
        var time = new ManualTime();
        using var server = new PoolWsServer();
        await using var pool = NewWsPool(time, idleSeconds: 30, server);

        await TakeAndReleaseAsync(pool);
        Assert.True(await Ports.WaitForAsync(() => pool.IdleCount >= 1, Timeout));
        time.Advance(TimeSpan.FromSeconds(31));
        Assert.True(pool.CloseIfIdle());
        int openedBefore = server.Opened;

        RawWebSocket socket = await TakeAsync(pool);
        try
        {
            Assert.True(server.Opened > openedBefore);
        }
        finally
        {
            socket.Abort();
        }
    }

    [Fact]
    public async Task WebSocketPool_WithInfiniteIdle_KeepsTheOldBehaviour()
    {
        var time = new ManualTime();
        using var server = new PoolWsServer();
        await using var pool = NewWsPool(time, idleSeconds: 0, server);

        await TakeAndReleaseAsync(pool);
        Assert.True(await Ports.WaitForAsync(() => pool.IdleCount >= 1, Timeout));

        time.Advance(TimeSpan.FromSeconds(31));

        Assert.False(pool.CloseIfIdle());
        Assert.Equal(1, pool.IdleCount);
        Assert.True(server.Live >= 1);
    }

    [Fact]
    public async Task CfWorkerPool_IdleLongerThanTheSetting_DropsFreeConnections()
    {
        var time = new ManualTime();
        using var server = new PoolWsServer();
        await using var pool = NewWorkerPool(time, idleSeconds: 30, server);

        await TakeAndReleaseWorkerAsync(pool);
        Assert.True(await Ports.WaitForAsync(() => pool.IdleCount >= 1, Timeout));
        Assert.True(server.Live >= 1);

        time.Advance(TimeSpan.FromSeconds(31));

        Assert.True(pool.CloseIfIdle());
        Assert.Equal(0, pool.IdleCount);
        Assert.True(await Ports.WaitForAsync(() => server.Live == 0, Timeout));
    }

    private static WebSocketPool NewWsPool(ManualTime time, int idleSeconds, PoolWsServer server)
    {
        var config = new EngineConfig
        {
            DcRedirects = new Dictionary<int, string> { [2] = "127.0.0.1" },
            PoolSize = 1,
            FallbackCfProxy = false,
            CfproxyH2Media = false,
            PoolIdleSeconds = idleSeconds,
        };
        return new WebSocketPool(config, new EngineLog(false, console: false), new EngineStats(), egress: null,
            timeProvider: time,
            connector: (_, _, path, ct) => ConnectWsAsync(server, path, ct));
    }

    private static async Task<RawWebSocket?> ConnectWsAsync(PoolWsServer server, string path, CancellationToken ct)
        => await RawWebSocket.ConnectAsync(
            "127.0.0.1", "example.test", Timeout, path, secure: false, port: server.Port, ct: ct);

    private static CfWorkerPool NewWorkerPool(ManualTime time, int idleSeconds, PoolWsServer server)
    {
        var config = new EngineConfig
        {
            PoolSize = 1,
            FallbackCfProxy = false,
            CfproxyH2Media = false,
            PoolIdleSeconds = idleSeconds,
        };
        return new CfWorkerPool(config, new EngineLog(false, console: false), new EngineStats(), egress: null,
            timeProvider: time,
            connector: (domains, _, _, ct) => ConnectWorkerAsync(server, domains, ct));
    }

    private static async Task<(RawWebSocket Socket, string WorkerDomain)?> ConnectWorkerAsync(
        PoolWsServer server, IReadOnlyList<string> domains, CancellationToken ct)
    {
        string domain = domains[0];
        RawWebSocket socket = await RawWebSocket.ConnectAsync(
            "127.0.0.1", "example.test", Timeout, "/apiws", secure: false, port: server.Port, ct: ct);
        return (socket, domain);
    }

    private static async Task TakeAndReleaseAsync(WebSocketPool pool)
    {
        RawWebSocket socket = await TakeAsync(pool);
        socket.Abort();
    }

    private static async Task TakeAndReleaseWorkerAsync(CfWorkerPool pool)
    {
        DateTime deadline = DateTime.UtcNow + Timeout;
        while (DateTime.UtcNow < deadline)
        {
            (RawWebSocket Socket, string WorkerDomain)? result =
                await pool.GetAsync(2, "127.0.0.1", ["worker.test"]);
            if (result is not null)
            {
                result.Value.Socket.Abort();
                return;
            }
            await Task.Delay(25);
        }
        throw new TimeoutException("CF worker pool never produced a socket");
    }

    private static async Task<RawWebSocket> TakeAsync(WebSocketPool pool)
    {
        DateTime deadline = DateTime.UtcNow + Timeout;
        while (DateTime.UtcNow < deadline)
        {
            RawWebSocket? socket = await pool.GetAsync(2, isMedia: false, isTestDc: false);
            if (socket is not null) return socket;
            await Task.Delay(25);
        }
        throw new TimeoutException("WS pool never produced a socket");
    }

    private sealed class ManualTime : TimeProvider
    {
        private DateTimeOffset _now = new(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);

        public override DateTimeOffset GetUtcNow() => _now;

        public void Advance(TimeSpan delta) => _now += delta;
    }

    /// <summary>A WebSocket peer that accepts any number of connections and keeps them open until the client leaves.</summary>
    private sealed class PoolWsServer : IDisposable
    {
        private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
        private readonly CancellationTokenSource _stop = new();
        private readonly ConcurrentDictionary<TcpClient, byte> _clients = [];
        private readonly Task _accepting;
        private int _opened;
        private int _closed;

        public int Port { get; }
        public int Opened => Volatile.Read(ref _opened);
        public int Live => Opened - Volatile.Read(ref _closed);

        public PoolWsServer()
        {
            _listener.Start();
            Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
            _accepting = Task.Run(AcceptLoopAsync);
        }

        private async Task AcceptLoopAsync()
        {
            try
            {
                while (!_stop.IsCancellationRequested)
                {
                    TcpClient client = await _listener.AcceptTcpClientAsync(_stop.Token);
                    _clients[client] = 0;
                    _ = Task.Run(() => ServeAsync(client));
                }
            }
            catch (Exception ex) when (ex is OperationCanceledException or SocketException or ObjectDisposedException)
            {
                // the test finished
            }
        }

        private async Task ServeAsync(TcpClient client)
        {
            Interlocked.Increment(ref _opened);
            try
            {
                NetworkStream stream = client.GetStream();
                if (!await ReadHeadersAsync(stream)) return;
                await stream.WriteAsync(
                    "HTTP/1.1 101 Switching Protocols\r\nUpgrade: websocket\r\nConnection: Upgrade\r\n\r\n"u8.ToArray());
                await stream.FlushAsync();
                while (!_stop.IsCancellationRequested)
                {
                    (int opcode, byte[] payload)? frame = await ReadFrameAsync(stream);
                    if (frame is null || frame.Value.opcode == 0x8) return;
                    if (frame.Value.opcode == 0x9)
                        await WriteFrameAsync(stream, 0xA, frame.Value.payload);
                }
            }
            catch (Exception ex) when (ex is IOException or SocketException or OperationCanceledException
                                           or ObjectDisposedException or EndOfStreamException)
            {
                // the pool aborted the socket
            }
            finally
            {
                _clients.TryRemove(client, out _);
                client.Dispose();
                Interlocked.Increment(ref _closed);
            }
        }

        private async Task<bool> ReadHeadersAsync(Stream stream)
        {
            var bytes = new List<byte>();
            var one = new byte[1];
            while (bytes.Count < 16 * 1024)
            {
                int read = await stream.ReadAsync(one, _stop.Token);
                if (read == 0) return false;
                bytes.Add(one[0]);
                if (bytes.Count >= 4 && bytes[^4] == '\r' && bytes[^3] == '\n' && bytes[^2] == '\r' && bytes[^1] == '\n')
                    return true;
            }
            return false;
        }

        private async Task<(int opcode, byte[] payload)?> ReadFrameAsync(Stream stream)
        {
            byte[] header;
            try
            {
                header = await ReadExactlyAsync(stream, 2);
            }
            catch (EndOfStreamException)
            {
                return null;
            }
            int opcode = header[0] & 0x0F;
            long length = header[1] & 0x7F;
            if (length == 126) length = BinaryPrimitives.ReadUInt16BigEndian(await ReadExactlyAsync(stream, 2));
            else if (length == 127)
                length = (long)BinaryPrimitives.ReadUInt64BigEndian(await ReadExactlyAsync(stream, 8));
            byte[] mask = (header[1] & 0x80) != 0 ? await ReadExactlyAsync(stream, 4) : [];
            byte[] payload = await ReadExactlyAsync(stream, (int)length);
            for (int i = 0; i < payload.Length && mask.Length == 4; i++) payload[i] ^= mask[i & 3];
            return (opcode, payload);
        }

        private async Task<byte[]> ReadExactlyAsync(Stream stream, int count)
        {
            var buffer = new byte[count];
            await stream.ReadExactlyAsync(buffer, _stop.Token);
            return buffer;
        }

        private static async Task WriteFrameAsync(Stream stream, int opcode, byte[] payload)
        {
            int headerLength = payload.Length < 126 ? 2 : 4;
            var frame = new byte[headerLength + payload.Length];
            frame[0] = (byte)(0x80 | opcode);
            if (payload.Length < 126)
            {
                frame[1] = (byte)payload.Length;
            }
            else
            {
                frame[1] = 126;
                BinaryPrimitives.WriteUInt16BigEndian(frame.AsSpan(2), (ushort)payload.Length);
            }
            payload.CopyTo(frame, headerLength);
            await stream.WriteAsync(frame);
            await stream.FlushAsync();
        }

        public void Dispose()
        {
            _stop.Cancel();
            _listener.Stop();
            foreach (TcpClient client in _clients.Keys) client.Dispose();
            try { _accepting.Wait(TimeSpan.FromSeconds(2)); } catch (AggregateException) { }
            _stop.Dispose();
        }
    }
}
