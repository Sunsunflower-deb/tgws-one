using System.Diagnostics;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using TgwsOne.Engine.Desync;

namespace TgwsOne.Engine.Tests.Desync;

public sealed class DesyncStreamTests
{
    private static readonly TimeSpan TestTimeout = TimeSpan.FromSeconds(20);

    [Fact]
    public async Task ProxyObserver_SeesTheSplitSegmentsSeparately()
    {
        using var timeout = new CancellationTokenSource(TestTimeout);
        byte[] hello = DesyncTestData.ClientHello();
        byte[] message = Encoding.ASCII.GetBytes("after the first flight");
        int splitAt = 10;

        var expected = new byte[hello.Length + message.Length];
        hello.CopyTo(expected, 0);
        message.CopyTo(expected, hello.Length);

        var target = new TcpListener(IPAddress.Loopback, 0);
        target.Start();
        try
        {
            int targetPort = ((IPEndPoint)target.LocalEndpoint).Port;
            Task<byte[]> targetRead = Task.Run(async () =>
            {
                using TcpClient accepted = await target.AcceptTcpClientAsync(timeout.Token);
                return await ReadExactlyAsync(accepted.GetStream(), expected.Length, timeout.Token);
            }, timeout.Token);

            using var observer = new ProxyObserver(targetPort);
            var strategy = new DesyncStrategy("split-pause",
                [new Split(new Absolute(splitAt)), new Pause(TimeSpan.FromMilliseconds(50))]);

            await using EgressConnection connection = await EgressSocket.ConnectAsync(
                "127.0.0.1", observer.Port, strategy, timeout: TimeSpan.FromSeconds(10), ct: timeout.Token);

            await connection.Stream.WriteAsync(hello, timeout.Token);
            await connection.Stream.WriteAsync(message, timeout.Token);
            await connection.Stream.FlushAsync(timeout.Token);

            byte[] received = await targetRead.WaitAsync(timeout.Token);
            Assert.Equal(expected, received);

            Assert.True(await Ports.WaitForAsync(() => observer.Count >= 2, TimeSpan.FromSeconds(5)),
                "the proxy never saw the two halves as separate reads");
            IReadOnlyList<ProxyObservation> observations = observer.Observations;
            Assert.Equal(splitAt, observations[0].Length);

            TimeSpan gap = TimeSpan.FromSeconds(
                (observations[1].Timestamp - observations[0].Timestamp) / (double)Stopwatch.Frequency);
            Assert.True(gap >= TimeSpan.FromMilliseconds(20),
                $"the halves arrived only {gap.TotalMilliseconds:0.0} ms apart");
        }
        finally
        {
            target.Stop();
        }
    }

    [Theory]
    [InlineData("split")]
    [InlineData("oob")]
    [InlineData("tlsrec")]
    public async Task TlsHandshake_Succeeds_AndTheServerGetsTheMessage(string kind)
    {
        using var timeout = new CancellationTokenSource(TestTimeout);
        byte[] message = Encoding.ASCII.GetBytes("hello over a desynced TLS handshake");
        using X509Certificate2 certificate = CreateCertificate();

        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        try
        {
            int port = ((IPEndPoint)listener.LocalEndpoint).Port;
            Task<byte[]> serverRead = Task.Run(async () =>
            {
                using TcpClient accepted = await listener.AcceptTcpClientAsync(timeout.Token);
                // The urgent byte is part of the ClientHello, so the receiver has to put it back into the stream.
                accepted.Client.SetSocketOption(
                    SocketOptionLevel.Socket, SocketOptionName.OutOfBandInline, true);

                using var serverTls = new SslStream(accepted.GetStream(), leaveInnerStreamOpen: false);
                await serverTls.AuthenticateAsServerAsync(
                    new SslServerAuthenticationOptions { ServerCertificate = certificate }, timeout.Token);

                byte[] buffer = new byte[message.Length];
                await serverTls.ReadExactlyAsync(buffer, timeout.Token);
                return buffer;
            }, timeout.Token);

            DesyncStrategy strategy = kind switch
            {
                "oob" => new DesyncStrategy("oob", [new Oob(new SniMid())]),
                "tlsrec" => new DesyncStrategy("tlsrec", [new TlsRec(new SniMid())]),
                _ => new DesyncStrategy("split", [new Split(new SniMid())]),
            };

            await using (EgressConnection connection = await EgressSocket.ConnectAsync(
                "127.0.0.1", port, strategy, timeout: TimeSpan.FromSeconds(10), ct: timeout.Token))
            {
                using var clientTls = new SslStream(
                    connection.Stream, leaveInnerStreamOpen: false, (_, _, _, _) => true);
                await clientTls.AuthenticateAsClientAsync(new SslClientAuthenticationOptions
                {
                    TargetHost = "desync.example.test",
                    EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13,
                }, timeout.Token);

                await clientTls.WriteAsync(message, timeout.Token);
                await clientTls.FlushAsync(timeout.Token);
            }

            Assert.Equal(message, await serverRead.WaitAsync(timeout.Token));
        }
        finally
        {
            listener.Stop();
        }
    }

    [Fact]
    public async Task NonTlsFirstWrite_PassesThroughUnchanged()
    {
        using var timeout = new CancellationTokenSource(TestTimeout);
        byte[] first = [0x17, 0x03, 0x03, 0x00, 0x08, 1, 2, 3, 4, 5, 6, 7, 8];
        byte[] second = [9, 10, 11, 12, 13];
        byte[] expected = [.. first, .. second];

        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        try
        {
            int port = ((IPEndPoint)listener.LocalEndpoint).Port;
            Task<byte[]> serverRead = ReadFromServerAsync(listener, expected.Length, timeout.Token);

            var strategy = new DesyncStrategy("split", [new Split(new Absolute(3))]);
            await using EgressConnection connection = await EgressSocket.ConnectAsync(
                "127.0.0.1", port, strategy, timeout: TimeSpan.FromSeconds(10), ct: timeout.Token);

            await connection.Stream.WriteAsync(first, timeout.Token);
            await connection.Stream.WriteAsync(second, timeout.Token);
            await connection.Stream.FlushAsync(timeout.Token);

            Assert.Equal(expected, await serverRead.WaitAsync(timeout.Token));
        }
        finally
        {
            listener.Stop();
        }
    }

    [Fact]
    public async Task WritesAfterTheFirstFlight_PassThroughUnchanged()
    {
        using var timeout = new CancellationTokenSource(TestTimeout);
        byte[] hello = DesyncTestData.ClientHello();
        byte[] second = Encoding.ASCII.GetBytes("second write");
        byte[] third = Encoding.ASCII.GetBytes("third write");
        byte[] expected = [.. hello, .. second, .. third];

        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        try
        {
            int port = ((IPEndPoint)listener.LocalEndpoint).Port;
            Task<byte[]> serverRead = ReadFromServerAsync(listener, expected.Length, timeout.Token);

            var strategy = new DesyncStrategy("split", [new Split(new Absolute(10))]);
            await using EgressConnection connection = await EgressSocket.ConnectAsync(
                "127.0.0.1", port, strategy, timeout: TimeSpan.FromSeconds(10), ct: timeout.Token);

            await connection.Stream.WriteAsync(hello, timeout.Token);
            await connection.Stream.WriteAsync(second, timeout.Token);
            await connection.Stream.WriteAsync(third, timeout.Token);
            await connection.Stream.FlushAsync(timeout.Token);

            Assert.Equal(expected, await serverRead.WaitAsync(timeout.Token));
        }
        finally
        {
            listener.Stop();
        }
    }

    [Fact]
    public async Task NullStrategy_PassesBytesThroughByteForByte()
    {
        using var timeout = new CancellationTokenSource(TestTimeout);
        byte[] hello = DesyncTestData.ClientHello();
        byte[] message = Encoding.ASCII.GetBytes("plain");
        byte[] expected = [.. hello, .. message];

        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        try
        {
            int port = ((IPEndPoint)listener.LocalEndpoint).Port;
            Task<byte[]> serverRead = ReadFromServerAsync(listener, expected.Length, timeout.Token);

            await using (EgressConnection connection = await EgressSocket.ConnectAsync(
                "127.0.0.1", port, strategy: null, timeout: TimeSpan.FromSeconds(10), ct: timeout.Token))
            {
                await connection.Stream.WriteAsync(hello, timeout.Token);
                await connection.Stream.WriteAsync(message, timeout.Token);
                await connection.Stream.FlushAsync(timeout.Token);
            }

            Assert.Equal(expected, await serverRead.WaitAsync(timeout.Token));
        }
        finally
        {
            listener.Stop();
        }
    }

    [Fact]
    public Task CancellationDuringPlan_CompletesWithoutUnobservedExceptions()
        => AssertNoUnobservedExceptionsAsync(async () =>
        {
            using var timeout = new CancellationTokenSource(TestTimeout);
            await using var server = new HangingServer();
            await using EgressConnection connection = await ConnectSlowAsync(server.Port, timeout.Token);
            using var cancel = new CancellationTokenSource(TimeSpan.FromMilliseconds(150));

            await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
                await connection.Stream.WriteAsync(DesyncTestData.ClientHello(), cancel.Token));
        });

    [Fact]
    public Task DisposeDuringPlan_CompletesWithoutUnobservedExceptions()
        => AssertNoUnobservedExceptionsAsync(async () =>
        {
            using var timeout = new CancellationTokenSource(TestTimeout);
            await using var server = new HangingServer();
            EgressConnection connection = await ConnectSlowAsync(server.Port, timeout.Token);
            Task write = connection.Stream.WriteAsync(DesyncTestData.ClientHello()).AsTask();
            try
            {
                // The plan has reached its ten-second pause once the server has read the first half.
                await server.FirstRead.WaitAsync(timeout.Token);

                await connection.DisposeAsync();
                try
                {
                    await write.WaitAsync(TimeSpan.FromSeconds(5));
                }
                catch (TimeoutException)
                {
                    throw;
                }
                catch
                {
                    // Awaiting observes the disposal outcome; cancellation or a closed socket both count.
                }
            }
            finally
            {
                await connection.DisposeAsync();
            }
        });

    private static Task<EgressConnection> ConnectSlowAsync(int port, CancellationToken ct)
    {
        var strategy = new DesyncStrategy("slow",
            [new Split(new Absolute(10)), new Pause(TimeSpan.FromSeconds(10))]);
        return EgressSocket.ConnectAsync(
            "127.0.0.1", port, strategy, timeout: TimeSpan.FromSeconds(10), ct: ct);
    }

    private static Task<byte[]> ReadFromServerAsync(TcpListener listener, int length, CancellationToken ct)
        => Task.Run(async () =>
        {
            using TcpClient accepted = await listener.AcceptTcpClientAsync(ct);
            return await ReadExactlyAsync(accepted.GetStream(), length, ct);
        }, ct);

    private static async Task<byte[]> ReadExactlyAsync(Stream stream, int length, CancellationToken ct)
    {
        byte[] buffer = new byte[length];
        int offset = 0;
        while (offset < length)
        {
            int read = await stream.ReadAsync(buffer.AsMemory(offset), ct);
            if (read == 0) throw new IOException("peer closed before all bytes arrived");
            offset += read;
        }
        return buffer;
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

    private static async Task AssertNoUnobservedExceptionsAsync(Func<Task> action)
    {
        var observed = new List<Exception>();
        EventHandler<UnobservedTaskExceptionEventArgs> handler = (_, e) =>
        {
            lock (observed) observed.Add(e.Exception);
            e.SetObserved();
        };

        TaskScheduler.UnobservedTaskException += handler;
        try
        {
            await action();

            for (int pass = 0; pass < 3; pass++)
            {
                GC.Collect();
                GC.WaitForPendingFinalizers();
                await Task.Yield();
            }

            Exception[] relevant;
            lock (observed) relevant = observed.Where(IsFromDesyncLayer).ToArray();
            Assert.Empty(relevant);
        }
        finally
        {
            TaskScheduler.UnobservedTaskException -= handler;
        }
    }

    private static bool IsFromDesyncLayer(Exception exception)
    {
        IEnumerable<Exception> inner = exception is AggregateException aggregate
            ? aggregate.Flatten().InnerExceptions
            : [exception];
        foreach (Exception item in inner)
        {
            string? trace = item.StackTrace;
            if (trace is not null &&
                (trace.Contains("TgwsOne.Engine.Desync", StringComparison.Ordinal) ||
                 trace.Contains("TgwsOne.Engine.Tests.Desync", StringComparison.Ordinal)))
                return true;
        }
        return false;
    }

    private readonly record struct ProxyObservation(long Timestamp, int Length);

    /// <summary>Accepts connections and drains them, so a slow plan has a live socket to write its first half into.</summary>
    private sealed class HangingServer : IDisposable, IAsyncDisposable
    {
        private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
        private readonly CancellationTokenSource _stop = new();
        private readonly TaskCompletionSource _firstRead = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly Task _loop;
        private int _disposed;

        public HangingServer()
        {
            _listener.Start();
            Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
            _loop = Task.Run(AcceptAsync);
        }

        public int Port { get; }

        public Task FirstRead => _firstRead.Task;

        private async Task AcceptAsync()
        {
            try
            {
                while (!_stop.IsCancellationRequested)
                {
                    using TcpClient client = await _listener.AcceptTcpClientAsync(_stop.Token);
                    var buffer = new byte[4096];
                    while (!_stop.IsCancellationRequested)
                    {
                        int read = await client.GetStream().ReadAsync(buffer, _stop.Token);
                        if (read == 0) break;
                        _firstRead.TrySetResult();
                    }
                }
            }
            catch (Exception ex) when (ex is OperationCanceledException or SocketException or IOException
                                       or ObjectDisposedException)
            {
                // The server is only here to keep the socket open; stopping it is the normal exit.
            }
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
            _stop.Cancel();
            _listener.Stop();
            try { _loop.Wait(TimeSpan.FromSeconds(2)); }
            catch (AggregateException)
            {
                // The accept loop already classified its exit.
            }
            _stop.Dispose();
        }

        public async ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
            _stop.Cancel();
            _listener.Stop();
            try { await _loop.ConfigureAwait(false); }
            catch (Exception ex) when (ex is OperationCanceledException or SocketException or IOException
                                       or ObjectDisposedException)
            {
                // The accept loop already classified its exit.
            }
            _stop.Dispose();
        }
    }

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
}
