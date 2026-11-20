using System.Diagnostics;
using System.Net.Http;
using System.Net.Security;
using System.Net.Sockets;

namespace TgwsOne.Services;

/// <summary>Whether a TCP connection can be made at all: what separates "no internet" from "something cuts the handshake".</summary>
internal interface ITcpProbe
{
    /// <returns>True when the connection was made within <paramref name="timeout"/>.</returns>
    Task<bool> ConnectAsync(string host, int port, TimeSpan timeout, CancellationToken ct);
}

/// <summary>A TLS handshake with a server name: the thing DPI middleboxes look at and cut.</summary>
internal interface ITlsProbe
{
    /// <returns>How long connecting and shaking hands took, or null if it did not get through within <paramref name="timeout"/>.</returns>
    Task<TimeSpan?> HandshakeAsync(string host, int port, TimeSpan timeout, CancellationToken ct);
}

/// <summary>A download that must arrive whole: a throttled connection that stalls after the first few kilobytes fails it.</summary>
internal interface IHttpProbe
{
    /// <returns><c>Ok</c> only when all <paramref name="expectedBytes"/> arrived within <paramref name="timeout"/>.</returns>
    Task<(bool Ok, TimeSpan Elapsed)> DownloadAsync(Uri url, long expectedBytes, TimeSpan timeout, CancellationToken ct);
}

/// <summary>The real <see cref="ITcpProbe"/>.</summary>
internal sealed class SocketTcpProbe : ITcpProbe
{
    public async Task<bool> ConnectAsync(string host, int port, TimeSpan timeout, CancellationToken ct)
    {
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(ct);
        limit.CancelAfter(timeout);
        try
        {
            using var tcp = new TcpClient();
            await tcp.ConnectAsync(host, port, limit.Token);
            return true;
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return false;   // our own time limit
        }
        catch (Exception ex) when (ex is SocketException or IOException or ObjectDisposedException)
        {
            return false;   // refused, unreachable, or the name did not resolve
        }
    }
}

/// <summary>
/// The real <see cref="ITlsProbe"/>. Whether the certificate is good is not the question here, only whether the handshake gets
/// through (and the host is only asked to say hello, nothing is sent after it), so any certificate is accepted.
/// </summary>
internal sealed class SslTlsProbe : ITlsProbe
{
    public async Task<TimeSpan?> HandshakeAsync(string host, int port, TimeSpan timeout, CancellationToken ct)
    {
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(ct);
        limit.CancelAfter(timeout);
        var clock = Stopwatch.StartNew();
        try
        {
            using var tcp = new TcpClient();
            await tcp.ConnectAsync(host, port, limit.Token);
            await using var tls = new SslStream(tcp.GetStream(), leaveInnerStreamOpen: false);
            await tls.AuthenticateAsClientAsync(new SslClientAuthenticationOptions
            {
                TargetHost = host,
                RemoteCertificateValidationCallback = (_, _, _, _) => true,
            }, limit.Token);
            return clock.Elapsed;
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return null;   // our own time limit
        }
        catch (Exception ex) when (ex is SocketException or IOException or System.Security.Authentication.AuthenticationException or ObjectDisposedException)
        {
            return null;   // refused, reset by the middlebox, or no usable handshake
        }
    }
}

/// <summary>
/// The real <see cref="IHttpProbe"/>. It goes direct: a system proxy would carry the request past the very thing being measured.
/// </summary>
internal sealed class HttpDownloadProbe : IHttpProbe
{
    public async Task<(bool Ok, TimeSpan Elapsed)> DownloadAsync(Uri url, long expectedBytes, TimeSpan timeout, CancellationToken ct)
    {
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(ct);
        limit.CancelAfter(timeout);
        var clock = Stopwatch.StartNew();
        try
        {
            using var handler = new SocketsHttpHandler { UseProxy = false, AllowAutoRedirect = false };
            using var http = new HttpClient(handler, disposeHandler: false) { Timeout = Timeout.InfiniteTimeSpan };
            using HttpResponseMessage response = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, limit.Token);
            if (!response.IsSuccessStatusCode) return (false, clock.Elapsed);

            await using Stream body = await response.Content.ReadAsStreamAsync(limit.Token);
            var buffer = new byte[16 * 1024];
            long total = 0;
            while (total < expectedBytes)
            {
                int n = await body.ReadAsync(buffer, limit.Token);
                if (n == 0) break;   // the server closed early
                total += n;
            }
            return (total >= expectedBytes, clock.Elapsed);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return (false, clock.Elapsed);   // stalled: our own time limit
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or SocketException or ObjectDisposedException)
        {
            return (false, clock.Elapsed);
        }
    }
}
