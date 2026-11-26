using System.Diagnostics;
using System.Net.Http;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using TgwsOne.Core;
using TgwsOne.Engine;
using TgwsOne.Engine.Desync;
using TgwsOne.Engine.Transport;

namespace TgwsOne.Mobile;

/// <summary>
/// The real probes of the mobile build. Direct checks look at the network as an ordinary client would; the bypass
/// checks open the stream through <see cref="EgressSocket"/> with the strategy the engine would use right now, so a
/// cut direct handshake and a working bypass are told apart the same way the engine sees them.
/// </summary>
public sealed class MobileEgressProbes(MobileStorage storage, IDesyncControl control,
                                        Func<TimeSpan, CancellationToken, Task<bool>> selfCheck,
                                        string? cloudflareDomain = null) : IEgressProbes
{
    public string CloudflareDomain { get; } = cloudflareDomain
        ?? ProxySettingsStore.Load(IniFile.Load(storage.IniPath)).CfDomains.FirstOrDefault()
        ?? DomainBalancer.DefaultDomains[0];

    public async Task<bool> TcpAsync(string host, int port, TimeSpan timeout, CancellationToken ct)
    {
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(ct);
        limit.CancelAfter(timeout);
        try
        {
            using var tcp = new TcpClient();
            await tcp.ConnectAsync(AddressFor(host), port, limit.Token);
            return true;
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return false;
        }
        catch (Exception ex) when (ex is SocketException or IOException or ObjectDisposedException)
        {
            return false;
        }
    }

    public async Task<TimeSpan?> TlsAsync(string host, int port, TimeSpan timeout, CancellationToken ct)
    {
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(ct);
        limit.CancelAfter(timeout);
        var clock = Stopwatch.StartNew();
        try
        {
            using var tcp = new TcpClient();
            await tcp.ConnectAsync(AddressFor(host), port, limit.Token);
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
            return null;
        }
        catch (Exception ex) when (ex is SocketException or IOException or AuthenticationException
                                       or TimeoutException or ObjectDisposedException)
        {
            return null;
        }
    }

    public async Task<TimeSpan?> BypassHandshakeAsync(string host, int port, string route, TimeSpan timeout,
                                                      CancellationToken ct)
    {
        DesyncStrategy? strategy = control.Resolve(host, route);
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(ct);
        limit.CancelAfter(timeout);
        var clock = Stopwatch.StartNew();
        try
        {
            await using EgressConnection connection = await EgressSocket.ConnectAsync(
                AddressFor(host), port, strategy, timeout, ct: limit.Token);
            await using var tls = new SslStream(connection.Stream, leaveInnerStreamOpen: false);
            await tls.AuthenticateAsClientAsync(new SslClientAuthenticationOptions
            {
                TargetHost = host,
                RemoteCertificateValidationCallback = (_, _, _, _) => true,
            }, limit.Token);
            return clock.Elapsed;
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return null;
        }
        catch (Exception ex) when (ex is SocketException or IOException or AuthenticationException
                                       or TimeoutException or ObjectDisposedException)
        {
            return null;
        }
    }

    public async Task<(bool Ok, TimeSpan Elapsed)> DownloadAsync(Uri url, long expectedBytes, TimeSpan timeout,
                                                                 CancellationToken ct)
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
                if (n == 0) break;
                total += n;
            }
            return (total >= expectedBytes, clock.Elapsed);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return (false, clock.Elapsed);
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or SocketException or ObjectDisposedException)
        {
            return (false, clock.Elapsed);
        }
    }

    public Task<bool> SelfCheckAsync(TimeSpan timeout, CancellationToken ct) => selfCheck(timeout, ct);

    /// <summary>Telegram's WebSocket hosts are dialed by their published DC addresses, as the engine does.</summary>
    private static string AddressFor(string host)
    {
        const string prefix = "kws";
        const string suffix = ".web.telegram.org";
        if (host.StartsWith(prefix, StringComparison.Ordinal) && host.EndsWith(suffix, StringComparison.Ordinal))
        {
            int dot = host.IndexOf('.');
            if (dot > prefix.Length && int.TryParse(host[prefix.Length..dot], out int dc)
                && TelegramNetwork.DcDefaultIps.TryGetValue(dc, out string? address))
            {
                return address;
            }
        }
        return host;
    }
}
