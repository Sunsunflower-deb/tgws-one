using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using TgwsOne.Engine.Crypto;
using TgwsOne.Engine.Relay;

namespace TgwsOne.Engine.Tests;

public sealed class NetworkChangeTests
{
    private const string SecretHex = "0123456789abcdef0123456789abcdef";
    private static readonly byte[] Secret = Convert.FromHexString(SecretHex);
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

    [Fact]
    public async Task OnNetworkChanged_ClosesThePoolsAndTheLiveOutboundSocket_AndKeepsListening()
    {
        using var telegram = new HoldingTelegram();
        int port = Ports.Free();
        var pool = new CountingPool();
        var config = new EngineConfig
        {
            Host = "127.0.0.1",
            Port = port,
            Secret = SecretHex,
            DcRedirects = new Dictionary<int, string>(),
            PoolSize = 0,
            FallbackCfProxy = false,
            CfproxyH2Media = false,
            TcpFallbackPort = telegram.Port,
            TcpFallbackHostOverride = "127.0.0.1",
        };
        await using var engine = new ProxyEngine(config, new EngineLog(false, console: false), desync: null,
            idlePools: [pool]);
        await engine.StartAsync(CancellationToken.None);
        try
        {
            using var tcp = new TcpClient();
            using var timeout = new CancellationTokenSource(Timeout);
            await tcp.ConnectAsync(IPAddress.Loopback, port, timeout.Token);
            NetworkStream stream = tcp.GetStream();
            var client = new ClientHandshake(Secret, MtProtoConstants.ProtoIntermediate, 2);
            await stream.WriteAsync(client.Bytes, timeout.Token);

            byte[] payload = RandomNumberGenerator.GetBytes(16);
            byte[] packet = new byte[4 + payload.Length];
            BinaryPrimitives.WriteInt32LittleEndian(packet, payload.Length);
            payload.CopyTo(packet, 4);
            await stream.WriteAsync(client.Encrypt.Transform(packet), timeout.Token);
            await stream.FlushAsync(timeout.Token);

            await telegram.Received.Task.WaitAsync(TimeSpan.FromSeconds(5));
            byte[] echoed = new byte[packet.Length];
            await stream.ReadExactlyAsync(echoed, timeout.Token);
            Assert.Equal(packet, client.Decrypt.Transform(echoed));

            engine.OnNetworkChanged();

            Assert.Equal(1, pool.CloseCalls);
            Assert.True(await Ports.WaitForAsync(() => telegram.Closed, TimeSpan.FromSeconds(5)));

            // The session ends normally: EOF or a reset on the client side, never an exception from the engine.
            var probe = new byte[1];
            try
            {
                int read = await stream.ReadAsync(probe, timeout.Token);
                Assert.Equal(0, read);
            }
            catch (IOException)
            {
                // a reset is an acceptable end for a connection whose network went away
            }

            Assert.True(engine.IsRunning);
            Assert.True(Ports.Listening(port));
        }
        finally
        {
            await engine.StopAsync();
        }
    }

    [Fact]
    public async Task OnNetworkChanged_ForgetsTheTcpFallbackBackoff()
    {
        int deadPort = Ports.Free();
        var config = new EngineConfig
        {
            Host = "127.0.0.1",
            Port = Ports.Free(),
            Secret = SecretHex,
            DcRedirects = new Dictionary<int, string>(),
            PoolSize = 0,
            FallbackCfProxy = false,
            CfproxyH2Media = false,
            TcpFallbackPort = deadPort,
            TcpFallbackHostOverride = "127.0.0.1",
        };
        await using var engine = new ProxyEngine(config, new EngineLog(false, console: false));
        await engine.StartAsync(CancellationToken.None);
        try
        {
            using var tcp = new TcpClient();
            using var timeout = new CancellationTokenSource(Timeout);
            await tcp.ConnectAsync(IPAddress.Loopback, engine.Port, timeout.Token);
            NetworkStream stream = tcp.GetStream();
            var client = new ClientHandshake(Secret, MtProtoConstants.ProtoIntermediate, 2);
            await stream.WriteAsync(client.Bytes, timeout.Token);
            await stream.FlushAsync(timeout.Token);

            Assert.True(await Ports.WaitForAsync(() => engine.TcpFallbackBackoffCount > 0, TimeSpan.FromSeconds(5)));

            engine.OnNetworkChanged();

            Assert.Equal(0, engine.TcpFallbackBackoffCount);
        }
        finally
        {
            await engine.StopAsync();
        }
    }

    private sealed class CountingPool : IIdleConnectionPool
    {
        private int _closeCalls;

        public int CloseCalls => Volatile.Read(ref _closeCalls);

        public void CloseIdle() => Interlocked.Increment(ref _closeCalls);
    }

    /// <summary>A fake Telegram endpoint that answers one packet and then holds the relay open.</summary>
    private sealed class HoldingTelegram : IDisposable
    {
        private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
        private readonly CancellationTokenSource _stop = new();
        private readonly Task _serving;
        private volatile bool _closed;

        public int Port { get; }
        public bool Closed => _closed;
        public TaskCompletionSource Received { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public HoldingTelegram()
        {
            _listener.Start();
            Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
            _serving = Task.Run(ServeAsync);
        }

        private async Task ServeAsync()
        {
            try
            {
                using TcpClient client = await _listener.AcceptTcpClientAsync(_stop.Token);
                NetworkStream stream = client.GetStream();
                var init = new byte[64];
                await stream.ReadExactlyAsync(init, _stop.Token);
                var telegram = new TelegramSide(init);

                byte[] wireHeader = new byte[4];
                await stream.ReadExactlyAsync(wireHeader, _stop.Token);
                byte[] header = telegram.DecryptFromProxy.Transform(wireHeader);
                int length = BinaryPrimitives.ReadInt32LittleEndian(header);
                if (length < 0 || length > 1_000_000)
                    throw new InvalidOperationException($"bad length {length}");
                byte[] wireBody = new byte[length];
                await stream.ReadExactlyAsync(wireBody, _stop.Token);
                byte[] body = telegram.DecryptFromProxy.Transform(wireBody);
                byte[] packet = [.. header, .. body];

                await stream.WriteAsync(telegram.EncryptToProxy.Transform(packet), _stop.Token);
                await stream.FlushAsync(_stop.Token);
                Received.TrySetResult();

                var buffer = new byte[1];
                while (await stream.ReadAsync(buffer, _stop.Token) > 0) { }
            }
            catch (Exception ex) when (ex is OperationCanceledException or IOException or SocketException
                                           or ObjectDisposedException or EndOfStreamException)
            {
                // the engine dropped the connection, which is what the test asks it to do
            }
            catch (Exception ex)
            {
                Received.TrySetException(ex);
            }
            finally
            {
                _closed = true;
            }
        }

        public void Dispose()
        {
            _stop.Cancel();
            _listener.Stop();
            try { _serving.Wait(TimeSpan.FromSeconds(2)); } catch (AggregateException) { }
            _stop.Dispose();
        }
    }
}
