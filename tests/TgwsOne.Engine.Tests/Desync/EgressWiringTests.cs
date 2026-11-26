using System.Buffers.Binary;
using System.Diagnostics;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using TgwsOne.Engine.Desync;
using TgwsOne.Engine.Transport;

namespace TgwsOne.Engine.Tests.Desync;

public sealed class EgressWiringTests
{
    private static readonly TimeSpan TestTimeout = TimeSpan.FromSeconds(20);

    [Fact]
    public async Task SplitStrategy_ThroughDesyncEgress_SplitsTheClientHello()
    {
        using var timeout = new CancellationTokenSource(TestTimeout);
        using X509Certificate2 certificate = CreateCertificate();
        var target = new TcpListener(IPAddress.Loopback, 0);
        target.Start();
        try
        {
            int targetPort = ((IPEndPoint)target.LocalEndpoint).Port;
            Task serverHandshake = AcceptAndHandshakeAsync(target, certificate, timeout.Token);

            using var observer = new ProxyObserver(targetPort);
            var controller = new RecordingController(
                new DesyncStrategy("split", [new Split(new SniMid()), new Pause(TimeSpan.FromMilliseconds(50))]));
            var egress = new DesyncEgress(controller);

            await using Stream transport = await egress.ConnectAsync(
                "127.0.0.1", observer.Port, "ws", "desync.example.test",
                TimeSpan.FromSeconds(10), timeout.Token);
            var capture = new CaptureWriteStream(transport);
            using var clientTls = new SslStream(capture, leaveInnerStreamOpen: false, (_, _, _, _) => true);
            await clientTls.AuthenticateAsClientAsync(new SslClientAuthenticationOptions
            {
                TargetHost = "desync.example.test",
                EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13,
            }, timeout.Token);

            await serverHandshake.WaitAsync(timeout.Token);

            byte[] hello = Assert.IsType<byte[]>(capture.FirstWrite);
            Assert.True(ClientHelloLayout.TryParse(hello, out ClientHelloLayout layout));
            int? resolved = layout.Resolve(new SniMid());
            Assert.NotNull(resolved);
            int split = resolved.Value;

            Assert.Equal(1, controller.Calls);
            Assert.Equal("ws", controller.LastRoute);
            Assert.Equal("desync.example.test", controller.LastSni);

            Assert.True(await Ports.WaitForAsync(() => observer.Count >= 2, TimeSpan.FromSeconds(5)),
                $"the observer saw {observer.Count} read(s), expected a split");
            IReadOnlyList<ProxyObservation> observations = observer.Observations;
            Assert.Equal(split, observations[0].Length);
            Assert.True(observations[1].Timestamp > observations[0].Timestamp);
        }
        finally
        {
            target.Stop();
        }
    }

    [Fact]
    public async Task NullStrategy_ThroughDesyncEgress_SendsTheClientHelloAsOnePiece()
    {
        using var timeout = new CancellationTokenSource(TestTimeout);
        using X509Certificate2 certificate = CreateCertificate();
        var target = new TcpListener(IPAddress.Loopback, 0);
        target.Start();
        try
        {
            int targetPort = ((IPEndPoint)target.LocalEndpoint).Port;
            Task serverHandshake = AcceptAndHandshakeAsync(target, certificate, timeout.Token);

            using var observer = new ProxyObserver(targetPort);
            var controller = new RecordingController(strategy: null);
            var egress = new DesyncEgress(controller);

            await using Stream transport = await egress.ConnectAsync(
                "127.0.0.1", observer.Port, "ws", "desync.example.test",
                TimeSpan.FromSeconds(10), timeout.Token);
            var capture = new CaptureWriteStream(transport);
            using var clientTls = new SslStream(capture, leaveInnerStreamOpen: false, (_, _, _, _) => true);
            await clientTls.AuthenticateAsClientAsync(new SslClientAuthenticationOptions
            {
                TargetHost = "desync.example.test",
                EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13,
            }, timeout.Token);

            await serverHandshake.WaitAsync(timeout.Token);

            byte[] hello = Assert.IsType<byte[]>(capture.FirstWrite);
            Assert.Equal(1, controller.Calls);
            Assert.Equal("ws", controller.LastRoute);

            Assert.True(await Ports.WaitForAsync(() => observer.Count >= 1, TimeSpan.FromSeconds(5)),
                "the observer never saw the ClientHello");
            Assert.Equal(hello.Length, observer.Observations[0].Length);
        }
        finally
        {
            target.Stop();
        }
    }

    [Fact]
    public async Task NullEgress_NeverConsultsTheController_SoMaskingPathsStayPlain()
    {
        using var timeout = new CancellationTokenSource(TestTimeout);
        using var server = new FakeWsServer(EchoAsync);
        var controller = new RecordingController(
            new DesyncStrategy("split", [new Split(new SniMid())]));
        _ = new DesyncEgress(controller);

        await using RawWebSocket ws = await RawWebSocket.ConnectAsync(
            "127.0.0.1", "example.test", TimeSpan.FromSeconds(5),
            secure: false, port: server.Port, ct: timeout.Token, route: "ws-fronted", egress: null);

        byte[] message = RandomNumberGenerator.GetBytes(5000);
        await ws.SendAsync(message, timeout.Token);
        Assert.Equal(message, await ws.ReceiveAsync(timeout.Token));

        Assert.Equal(0, controller.Calls);
        Assert.Null(controller.LastRoute);
    }

    [Fact]
    public async Task RawWebSocket_WithEgress_PassesTheRouteAndSni()
    {
        using var timeout = new CancellationTokenSource(TestTimeout);
        using var server = new FakeWsServer(EchoAsync);
        var egress = new SpyEgress();

        await using RawWebSocket ws = await RawWebSocket.ConnectAsync(
            "127.0.0.1", "example.test", TimeSpan.FromSeconds(5),
            sni: "front.example", secure: false, port: server.Port,
            ct: timeout.Token, route: "ws-fronted", egress: egress);

        byte[] message = RandomNumberGenerator.GetBytes(128);
        await ws.SendAsync(message, timeout.Token);
        Assert.Equal(message, await ws.ReceiveAsync(timeout.Token));

        Assert.Equal(1, egress.Calls);
        Assert.Equal("127.0.0.1", egress.Host);
        Assert.Equal(server.Port, egress.Port);
        Assert.Equal("ws-fronted", egress.Route);
        Assert.Equal("front.example", egress.Sni);
    }

    [Fact]
    public async Task WithoutEgress_TheDirectSocketPathIsUnchanged()
    {
        using var timeout = new CancellationTokenSource(TestTimeout);
        using var server = new FakeWsServer(EchoAsync);

        await using RawWebSocket ws = await RawWebSocket.ConnectAsync(
            "127.0.0.1", "example.test", TimeSpan.FromSeconds(5),
            secure: false, port: server.Port, ct: timeout.Token);

        byte[] message = RandomNumberGenerator.GetBytes(5000);
        await ws.SendAsync(message, timeout.Token);
        Assert.Equal(message, await ws.ReceiveAsync(timeout.Token));
    }

    private static async Task AcceptAndHandshakeAsync(
        TcpListener listener, X509Certificate2 certificate, CancellationToken ct)
    {
        using TcpClient accepted = await listener.AcceptTcpClientAsync(ct);
        using var tls = new SslStream(accepted.GetStream(), leaveInnerStreamOpen: false);
        await tls.AuthenticateAsServerAsync(
            new SslServerAuthenticationOptions { ServerCertificate = certificate }, ct);
    }

    private static async Task EchoAsync(NetworkStream stream, CancellationToken ct)
    {
        await ReadHeadersAsync(stream, ct);
        await stream.WriteAsync(Encoding.ASCII.GetBytes(
            "HTTP/1.1 101 Switching Protocols\r\nUpgrade: websocket\r\nConnection: Upgrade\r\n\r\n"), ct);
        await stream.FlushAsync(ct);
        while (true)
        {
            (int opcode, byte[] payload) = await ReadFrameAsync(stream, ct);
            if (opcode == 0x8) return;
            await WriteFrameAsync(stream, 0x2, payload, ct);
        }
    }

    private static X509Certificate2 CreateCertificate()
    {
        using var rsa = RSA.Create(2048);
        var request = new CertificateRequest(
            "CN=desync.example.test", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, false));
        request.CertificateExtensions.Add(new X509SubjectKeyIdentifierExtension(request.PublicKey, false));
        request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(
            new OidCollection { new("1.3.6.1.5.5.7.3.1") }, false));

        using X509Certificate2 self = request.CreateSelfSigned(
            DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(1));
        return X509CertificateLoader.LoadPkcs12(self.Export(X509ContentType.Pfx), null);
    }

    private sealed class RecordingController(DesyncStrategy? strategy) : IDesyncController
    {
        public int Calls { get; private set; }
        public string? LastSni { get; private set; }
        public string? LastRoute { get; private set; }

        public DesyncStrategy? Resolve(string sni, string route)
        {
            Calls++;
            LastSni = sni;
            LastRoute = route;
            return strategy;
        }

        public void Report(string sni, string route, string? strategyId, bool ok, EgressFailure failure,
                           TimeSpan elapsed)
        {
        }
    }

    private sealed class SpyEgress : IEgress
    {
        public int Calls { get; private set; }
        public string? Host { get; private set; }
        public int Port { get; private set; }
        public string? Route { get; private set; }
        public string? Sni { get; private set; }

        public Task<Stream> ConnectAsync(string host, int port, string route, string sni, TimeSpan timeout,
                                         CancellationToken ct)
        {
            Calls++;
            Host = host;
            Port = port;
            Route = route;
            Sni = sni;
            return ConnectCoreAsync(host, port, ct);
        }

        private static async Task<Stream> ConnectCoreAsync(string host, int port, CancellationToken ct)
        {
            var socket = new Socket(SocketType.Stream, ProtocolType.Tcp);
            try
            {
                await socket.ConnectAsync(host, port, ct).ConfigureAwait(false);
                return new NetworkStream(socket, ownsSocket: true);
            }
            catch
            {
                socket.Dispose();
                throw;
            }
        }
    }

    /// <summary>Records the first write, which for a TLS client is the ClientHello.</summary>
    private sealed class CaptureWriteStream(Stream inner) : Stream
    {
        private byte[]? _firstWrite;

        public byte[]? FirstWrite => Volatile.Read(ref _firstWrite);

        public override bool CanRead => inner.CanRead;
        public override bool CanSeek => false;
        public override bool CanWrite => inner.CanWrite;
        public override long Length => throw new NotSupportedException();
        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override int Read(byte[] buffer, int offset, int count) => inner.Read(buffer, offset, count);
        public override int Read(Span<byte> buffer) => inner.Read(buffer);
        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken ct)
            => inner.ReadAsync(buffer, offset, count, ct);
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
            => inner.ReadAsync(buffer, ct);

        public override void Write(byte[] buffer, int offset, int count) => Write(buffer.AsSpan(offset, count));
        public override void Write(ReadOnlySpan<byte> buffer)
        {
            Capture(buffer);
            inner.Write(buffer);
        }

        public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken ct)
            => WriteAsync(buffer.AsMemory(offset, count), ct).AsTask();
        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken ct = default)
        {
            Capture(buffer.Span);
            return inner.WriteAsync(buffer, ct);
        }

        private void Capture(ReadOnlySpan<byte> buffer)
        {
            if (buffer.IsEmpty || Volatile.Read(ref _firstWrite) is not null) return;
            Interlocked.CompareExchange(ref _firstWrite, buffer.ToArray(), null);
        }

        public override void Flush() => inner.Flush();
        public override Task FlushAsync(CancellationToken ct) => inner.FlushAsync(ct);
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (disposing) inner.Dispose();
            base.Dispose(disposing);
        }

        public override async ValueTask DisposeAsync()
        {
            await inner.DisposeAsync().ConfigureAwait(false);
            GC.SuppressFinalize(this);
        }
    }

    private readonly record struct ProxyObservation(long Timestamp, int Length);

    /// <summary>Accepts one client, dials the target, and records every read of the client-to-target direction.</summary>
    private sealed class ProxyObserver : IDisposable
    {
        private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
        private readonly int _targetPort;
        private readonly CancellationTokenSource _stop = new();
        private readonly List<ProxyObservation> _observations = [];
        private readonly object _gate = new();
        private readonly Task _loop;

        public ProxyObserver(int targetPort)
        {
            _targetPort = targetPort;
            _listener.Start();
            Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
            _loop = Task.Run(AcceptAsync);
        }

        public int Port { get; }

        public int Count
        {
            get { lock (_gate) return _observations.Count; }
        }

        public IReadOnlyList<ProxyObservation> Observations
        {
            get { lock (_gate) return _observations.ToArray(); }
        }

        private async Task AcceptAsync()
        {
            try
            {
                using TcpClient client = await _listener.AcceptTcpClientAsync(_stop.Token);
                using var upstream = new TcpClient();
                await upstream.ConnectAsync(IPAddress.Loopback, _targetPort, _stop.Token);

                Task up = RelayAsync(client.GetStream(), upstream.GetStream(), observe: true, _stop.Token);
                Task down = RelayAsync(upstream.GetStream(), client.GetStream(), observe: false, _stop.Token);
                await Task.WhenAll(up, down);
            }
            catch (Exception ex) when (ex is OperationCanceledException or SocketException or IOException
                                       or ObjectDisposedException)
            {
                // Disposing the observer tears the relay down; that is the normal exit.
            }
        }

        private async Task RelayAsync(Stream from, Stream to, bool observe, CancellationToken ct)
        {
            var buffer = new byte[64 * 1024];
            try
            {
                while (true)
                {
                    int read = await from.ReadAsync(buffer, ct);
                    if (read == 0) return;

                    if (observe)
                    {
                        lock (_gate) _observations.Add(new ProxyObservation(Stopwatch.GetTimestamp(), read));
                    }
                    await to.WriteAsync(buffer.AsMemory(0, read), ct);
                }
            }
            catch (Exception ex) when (ex is OperationCanceledException or SocketException or IOException
                                       or ObjectDisposedException)
            {
                // The other side went away first; there is nothing left to relay.
            }
        }

        public void Dispose()
        {
            _stop.Cancel();
            _listener.Stop();
            try { _loop.Wait(TimeSpan.FromSeconds(2)); }
            catch (AggregateException)
            {
                // The relay already recorded why it stopped; disposing must not throw.
            }
            _stop.Dispose();
        }
    }

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

    private static async Task ReadHeadersAsync(Stream stream, CancellationToken ct)
    {
        var bytes = new List<byte>();
        var one = new byte[1];
        while (bytes.Count < 16 * 1024)
        {
            int read = await stream.ReadAsync(one, ct);
            if (read == 0) return;
            bytes.Add(one[0]);
            if (bytes.Count >= 4 && bytes[^4] == '\r' && bytes[^3] == '\n' && bytes[^2] == '\r' && bytes[^1] == '\n')
                return;
        }
    }

    private static async Task<(int Opcode, byte[] Payload)> ReadFrameAsync(Stream stream, CancellationToken ct)
    {
        byte[] header = await ReadExactlyAsync(stream, 2, ct);
        int opcode = header[0] & 0x0F;
        long length = header[1] & 0x7F;
        if (length == 126) length = BinaryPrimitives.ReadUInt16BigEndian(await ReadExactlyAsync(stream, 2, ct));
        else if (length == 127)
            length = (long)BinaryPrimitives.ReadUInt64BigEndian(await ReadExactlyAsync(stream, 8, ct));
        byte[] maskKey = (header[1] & 0x80) != 0 ? await ReadExactlyAsync(stream, 4, ct) : [];
        byte[] payload = await ReadExactlyAsync(stream, (int)length, ct);
        if (maskKey.Length == 4)
        {
            for (int i = 0; i < payload.Length; i++) payload[i] ^= maskKey[i & 3];
        }
        return (opcode, payload);
    }

    private static async Task WriteFrameAsync(Stream stream, int opcode, byte[] payload, CancellationToken ct)
    {
        int headerLength = payload.Length < 126 ? 2 : payload.Length < 65536 ? 4 : 10;
        var frame = new byte[headerLength + payload.Length];
        frame[0] = (byte)(0x80 | opcode);
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
        await stream.WriteAsync(frame, ct);
        await stream.FlushAsync(ct);
    }

    private static async Task<byte[]> ReadExactlyAsync(Stream stream, int count, CancellationToken ct)
    {
        var buffer = new byte[count];
        await stream.ReadExactlyAsync(buffer, ct);
        return buffer;
    }
}
