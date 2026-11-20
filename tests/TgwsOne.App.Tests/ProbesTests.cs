using System.Diagnostics;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using TgwsOne.Services;

namespace TgwsOne.Tests;

/// <summary>The real probes against servers on loopback: what a handshake or a download that is cut or throttled looks like from the client's side.</summary>
public class ProbesTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static int FreePort()
    {
        var l = new TcpListener(IPAddress.Loopback, 0);
        l.Start();
        int port = ((IPEndPoint)l.LocalEndpoint).Port;
        l.Stop();
        return port;
    }

    // ----- TLS -----

    private enum Behaviour { Handshake, Silent, CloseAtOnce }

    /// <summary>A TLS server: completes the handshake with a throwaway certificate, or says nothing, or hangs up.</summary>
    private sealed class TlsServer : IDisposable
    {
        private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
        private readonly CancellationTokenSource _stop = new();
        private readonly X509Certificate2 _certificate;

        public TlsServer(Behaviour behaviour)
        {
            using var rsa = RSA.Create(2048);
            var request = new CertificateRequest("CN=localhost", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
            using X509Certificate2 self = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(1));
            _certificate = X509CertificateLoader.LoadPkcs12(self.Export(X509ContentType.Pfx), null);

            _listener.Start();
            Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
            _ = Task.Run(() => AcceptLoop(behaviour));
        }

        public int Port { get; }

        private async Task AcceptLoop(Behaviour behaviour)
        {
            try
            {
                while (!_stop.IsCancellationRequested)
                {
                    TcpClient client = await _listener.AcceptTcpClientAsync(_stop.Token);
                    _ = Task.Run(() => Serve(client, behaviour));
                }
            }
            catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException or SocketException) { }
        }

        private async Task Serve(TcpClient client, Behaviour behaviour)
        {
            using (client)
            {
                try
                {
                    switch (behaviour)
                    {
                        case Behaviour.CloseAtOnce:
                            return;
                        case Behaviour.Silent:
                            await Task.Delay(Timeout.Infinite, _stop.Token);
                            return;
                        default:
                            await using (var tls = new SslStream(client.GetStream()))
                                await tls.AuthenticateAsServerAsync(_certificate);
                            return;
                    }
                }
                catch (Exception ex) when (ex is IOException or OperationCanceledException or System.Security.Authentication.AuthenticationException) { }
            }
        }

        public void Dispose()
        {
            _stop.Cancel();
            _listener.Stop();
            _certificate.Dispose();
        }
    }

    [Fact]
    public async Task Tls_AHandshakeThatGetsThrough_ReturnsHowLongItTook()
    {
        using var server = new TlsServer(Behaviour.Handshake);

        TimeSpan? took = await new SslTlsProbe().HandshakeAsync("127.0.0.1", server.Port, TimeSpan.FromSeconds(5), Ct);

        Assert.NotNull(took);
        Assert.InRange(took.Value, TimeSpan.Zero, TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task Tls_AClosedPort_IsNull()
    {
        TimeSpan? took = await new SslTlsProbe().HandshakeAsync("127.0.0.1", FreePort(), TimeSpan.FromSeconds(5), Ct);

        Assert.Null(took);
    }

    [Fact]
    public async Task Tls_AServerThatHangsUp_IsNull()
    {
        using var server = new TlsServer(Behaviour.CloseAtOnce);

        TimeSpan? took = await new SslTlsProbe().HandshakeAsync("127.0.0.1", server.Port, TimeSpan.FromSeconds(5), Ct);

        Assert.Null(took);
    }

    [Fact]
    public async Task Tls_AServerThatNeverAnswers_IsNull_WhenTheTimeIsUp_NotBefore_AndNotMuchAfter()
    {
        // A middlebox that swallows the ClientHello leaves exactly this: the connection is open and nothing comes back.
        using var server = new TlsServer(Behaviour.Silent);
        var clock = Stopwatch.StartNew();

        TimeSpan? took = await new SslTlsProbe().HandshakeAsync("127.0.0.1", server.Port, TimeSpan.FromMilliseconds(400), Ct);

        Assert.Null(took);
        Assert.InRange(clock.Elapsed, TimeSpan.FromMilliseconds(350), TimeSpan.FromSeconds(4));
    }

    [Fact]
    public async Task Tls_WhenTheCallerCancels_TheCancellationIsPassedOn()
    {
        using var server = new TlsServer(Behaviour.Silent);
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        cts.CancelAfter(150);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => new SslTlsProbe().HandshakeAsync("127.0.0.1", server.Port, TimeSpan.FromSeconds(30), cts.Token));
    }

    // ----- download -----

    private sealed class HttpServer : IDisposable
    {
        private readonly HttpListener _listener = new();
        private readonly CancellationTokenSource _stop = new();

        public HttpServer(Func<HttpListenerContext, CancellationToken, Task> handler)
        {
            int port = FreePort();
            Url = new Uri($"http://127.0.0.1:{port}/");
            _listener.Prefixes.Add(Url.ToString());
            _listener.Start();
            _ = Task.Run(async () =>
            {
                try
                {
                    while (!_stop.IsCancellationRequested)
                    {
                        HttpListenerContext ctx = await _listener.GetContextAsync();
                        _ = Task.Run(async () =>
                        {
                            try { await handler(ctx, _stop.Token); }
                            catch (Exception ex) when (ex is HttpListenerException or IOException or OperationCanceledException or ObjectDisposedException) { }
                        });
                    }
                }
                catch (Exception ex) when (ex is HttpListenerException or ObjectDisposedException or InvalidOperationException) { }
            });
        }

        public Uri Url { get; }

        public void Dispose()
        {
            _stop.Cancel();
            _listener.Close();
        }
    }

    private static async Task Send(HttpListenerContext ctx, int bytes, bool close = true)
    {
        ctx.Response.ContentLength64 = bytes;
        await ctx.Response.OutputStream.WriteAsync(new byte[bytes]);
        if (close) ctx.Response.Close();
    }

    [Fact]
    public async Task Download_TheWholeBody_IsOk()
    {
        using var server = new HttpServer((ctx, _) => Send(ctx, 65536));

        (bool ok, TimeSpan elapsed) = await new HttpDownloadProbe().DownloadAsync(server.Url, 65536, TimeSpan.FromSeconds(5), Ct);

        Assert.True(ok);
        Assert.InRange(elapsed, TimeSpan.Zero, TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task Download_ABodyThatEndsEarly_IsNotOk()
    {
        using var server = new HttpServer((ctx, _) => Send(ctx, 1000));

        (bool ok, _) = await new HttpDownloadProbe().DownloadAsync(server.Url, 65536, TimeSpan.FromSeconds(5), Ct);

        Assert.False(ok);
    }

    [Fact]
    public async Task Download_ThatFreezesAfter16Kilobytes_IsNotOk_WhenTheTimeIsUp()
    {
        // The throttling Cloudflare connections suffer under some providers: the first 16 KB flow, then nothing. A probe that
        // only checked "did we connect" would call this a success.
        using var server = new HttpServer(async (ctx, stop) =>
        {
            ctx.Response.ContentLength64 = 65536;
            await ctx.Response.OutputStream.WriteAsync(new byte[16 * 1024], stop);
            await ctx.Response.OutputStream.FlushAsync(stop);
            await Task.Delay(Timeout.Infinite, stop);
        });
        var clock = Stopwatch.StartNew();

        (bool ok, _) = await new HttpDownloadProbe().DownloadAsync(server.Url, 65536, TimeSpan.FromMilliseconds(500), Ct);

        Assert.False(ok);
        Assert.InRange(clock.Elapsed, TimeSpan.FromMilliseconds(450), TimeSpan.FromSeconds(4));
    }

    [Fact]
    public async Task Download_AnErrorStatus_IsNotOk()
    {
        using var server = new HttpServer((ctx, _) =>
        {
            ctx.Response.StatusCode = 500;
            ctx.Response.Close();
            return Task.CompletedTask;
        });

        (bool ok, _) = await new HttpDownloadProbe().DownloadAsync(server.Url, 65536, TimeSpan.FromSeconds(5), Ct);

        Assert.False(ok);
    }

    [Fact]
    public async Task Download_NobodyThere_IsNotOk()
    {
        (bool ok, _) = await new HttpDownloadProbe().DownloadAsync(new Uri($"http://127.0.0.1:{FreePort()}/"), 65536, TimeSpan.FromSeconds(5), Ct);

        Assert.False(ok);
    }

    [Fact]
    public async Task Download_WhenTheCallerCancels_TheCancellationIsPassedOn()
    {
        using var server = new HttpServer(async (ctx, stop) =>
        {
            ctx.Response.ContentLength64 = 65536;
            await ctx.Response.OutputStream.FlushAsync(stop);
            await Task.Delay(Timeout.Infinite, stop);
        });
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        cts.CancelAfter(150);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => new HttpDownloadProbe().DownloadAsync(server.Url, 65536, TimeSpan.FromSeconds(30), cts.Token));
    }
}
