using System.Buffers.Binary;
using System.Net;
using System.Net.Http.Headers;
using System.Threading.Channels;
using TgwsOne.Engine.Crypto;
using TgwsOne.Engine.MtProto;

namespace TgwsOne.Engine.Transport;

/// <summary>An MTProto endpoint error that the native client must receive as a negative code, not just as EOF.</summary>
public sealed class MtProtoTransportException(int code, string source) : Exception($"{code} ({source})")
{
    public int Code { get; } = code;
}

/// <summary>
/// The media route through Cloudflare's HTTP/2 endpoint (<c>POST https://kwsN.{cf-domain}/api</c>). One request
/// carries one MTProto packet; a channel multiplexes up to eight requests at a time over one HTTP/2 connection.
/// .NET's <see cref="HttpClient"/> does the multiplexing and flow control that upstream implemented by hand on
/// top of httpx/h2; the bounds, status handling and error mapping are the same.
/// </summary>
public sealed class CfH2Pool : IAsyncDisposable
{
    private const int MaxChannelRequests = 8;
    private const int MaxChannelBytes = 8 * 1024 * 1024;
    private const double DomainCooldownSeconds = 30;
    private static readonly TimeSpan SetupTimeout = TimeSpan.FromSeconds(8);

    private readonly EngineConfig _config;
    private readonly EngineLog _log;
    private readonly EngineStats _stats;
    private readonly DomainBalancer _balancer;
    private readonly object _gate = new();
    private readonly Dictionary<string, CfH2Lane> _lanes = [];
    private readonly Dictionary<string, SemaphoreSlim> _locks = [];
    private readonly Dictionary<string, double> _failedUntil = [];
    private readonly Dictionary<int, double> _setupRetryAfter = [];
    private int _nextLane;
    private int _nextChannel;
    private bool _closed;

    public CfH2Pool(EngineConfig config, EngineLog log, EngineStats stats, DomainBalancer balancer)
    {
        _config = config;
        _log = log;
        _stats = stats;
        _balancer = balancer;
    }

    private static double Now() => Environment.TickCount64 / 1000.0;

    public async Task<CfH2Channel?> OpenAsync(int dc, string label, CancellationToken ct)
    {
        if (_closed) throw new InvalidOperationException("H2 pool closed");
        lock (_gate)
        {
            if (Now() < _setupRetryAfter.GetValueOrDefault(dc)) return null;
        }

        using var setup = CancellationTokenSource.CreateLinkedTokenSource(ct);
        setup.CancelAfter(SetupTimeout);
        try
        {
            return await OpenCoreAsync(dc, label, setup.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            lock (_gate) _setupRetryAfter[dc] = Now() + DomainCooldownSeconds;
            _stats.H2Error();
            _log.Warn($"[{label}] H2 DC{dc} setup timed out; using WS for {DomainCooldownSeconds:0}s");
            return null;
        }
    }

    private async Task<CfH2Channel?> OpenCoreAsync(int dc, string label, CancellationToken ct)
    {
        if (!TelegramNetwork.DcDefaultIps.ContainsKey(dc))
        {
            _log.Warn($"[{label}] H2 unsupported DC{dc} -> existing WS route");
            return null;
        }

        SemaphoreSlim hostLock;
        CfH2Lane? lane;
        string host;
        string baseDomain;
        lock (_gate)
        {
            baseDomain = _balancer.GetDomainsForDc(dc).FirstOrDefault() ?? "";
        }
        if (baseDomain.Length == 0) return null;

        foreach (string domain in _balancer.GetDomainsForDc(dc))
        {
            host = $"kws{dc}.{domain}";
            lock (_gate)
            {
                if (!_locks.TryGetValue(host, out hostLock!))
                {
                    hostLock = new SemaphoreSlim(1, 1);
                    _locks[host] = hostLock;
                }
            }
            await hostLock.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                bool closed;
                double failedUntil;
                lock (_gate)
                {
                    closed = _closed;
                    lane = _lanes.GetValueOrDefault(host);
                    failedUntil = Math.Max(_failedUntil.GetValueOrDefault(host), lane?.FailedUntil ?? 0);
                }
                if (closed) throw new InvalidOperationException("H2 pool closed");
                if (Now() < failedUntil) continue;

                if (lane is null)
                {
                    int laneId = Interlocked.Increment(ref _nextLane);
                    lane = new CfH2Lane(host, laneId, _config, _log, _stats);
                    try
                    {
                        await lane.PreflightAsync(ct).ConfigureAwait(false);
                    }
                    catch (Exception ex) when (ex is HttpRequestException or IOException or InvalidDataException)
                    {
                        _stats.H2Error();
                        lock (_gate) _failedUntil[host] = Now() + DomainCooldownSeconds;
                        _log.Warn($"[{label}] H2 lane={laneId} preflight failed host={host}: {ex.GetType().Name}: {ex.Message}");
                        await lane.DisposeAsync().ConfigureAwait(false);
                        continue;
                    }
                    catch
                    {
                        await lane.DisposeAsync().ConfigureAwait(false);
                        throw;
                    }
                    lock (_gate) _lanes[host] = lane;
                }

                var channel = new CfH2Channel(lane, Interlocked.Increment(ref _nextChannel), label, _log, _stats);
                _balancer.UpdateDomainForDc(dc, domain);
                _log.Info($"[{label}] ROUTE DC{dc} media -> H2 lane={lane.LaneId} channel={channel.Id} host={host}");
                return channel;
            }
            finally
            {
                hostLock.Release();
            }
        }

        _log.Debug($"[{label}] H2 DC{dc} unavailable; using WS");
        return null;
    }

    public async ValueTask DisposeAsync()
    {
        List<CfH2Lane> lanes;
        lock (_gate)
        {
            if (_closed) return;
            _closed = true;
            lanes = [.. _lanes.Values];
            _lanes.Clear();
        }
        foreach (CfH2Lane lane in lanes) await lane.DisposeAsync().ConfigureAwait(false);
    }
}

internal sealed class CfH2Lane : IAsyncDisposable
{
    private const int BufferBytes = 16 * 1024 * 1024;

    public string Host { get; }
    public int LaneId { get; }
    public double FailedUntil { get; set; }

    private readonly HttpClient _client;
    private readonly EngineLog _log;
    private readonly EngineStats _stats;

    public CfH2Lane(string host, int laneId, EngineConfig config, EngineLog log, EngineStats stats)
    {
        Host = host;
        LaneId = laneId;
        _log = log;
        _stats = stats;

        var handler = new SocketsHttpHandler
        {
            UseProxy = false,
            ConnectTimeout = TimeSpan.FromSeconds(8),
            EnableMultipleHttp2Connections = true,
            AutomaticDecompression = DecompressionMethods.None,
            MaxResponseDrainSize = BufferBytes,
            PooledConnectionIdleTimeout = TimeSpan.FromSeconds(65),
        };
        if (config.DisableSecure)
            throw new InvalidOperationException("H2 requires TLS");
        _client = new HttpClient(handler)
        {
            Timeout = TimeSpan.FromSeconds(65),
            DefaultRequestVersion = HttpVersion.Version20,
            DefaultVersionPolicy = HttpVersionPolicy.RequestVersionExact,
        };
    }

    public async Task PreflightAsync(CancellationToken ct)
    {
        double started = Environment.TickCount64 / 1000.0;
        using var request = new HttpRequestMessage(HttpMethod.Head, $"https://{Host}/api");
        using HttpResponseMessage response = await _client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct)
            .ConfigureAwait(false);
        if (response.Version != HttpVersion.Version20)
            throw new InvalidDataException($"CF /api preflight negotiated {response.Version}");
        int status = (int)response.StatusCode;
        if (!(status is >= 200 and < 300) && status is not (405 or 501))
            throw new HttpRequestException($"CF /api preflight HTTP {status} " +
                                           $"cf_ray={(response.Headers.TryGetValues("cf-ray", out IEnumerable<string>? r) ? r.FirstOrDefault() : null) ?? "-"}");
        _stats.H2TcpConnection();
        _log.Debug($"H2 lane={LaneId} ready host={Host} status={status} " +
                   $"ms={(Environment.TickCount64 / 1000.0 - started) * 1000:0}");
    }

    public async Task<byte[]> PostAsync(byte[] body, int channelId, CancellationToken ct)
    {
        _stats.H2Request();
        using var request = new HttpRequestMessage(HttpMethod.Post, $"https://{Host}/api")
        {
            Content = new ByteArrayContent(body),
        };
        request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        request.Headers.AcceptEncoding.Add(new StringWithQualityHeaderValue("identity"));

        using HttpResponseMessage response = await _client.SendAsync(
            request, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        string cfRay = response.Headers.TryGetValues("cf-ray", out IEnumerable<string>? rays)
            ? rays.FirstOrDefault() ?? "-"
            : "-";
        if (response.Version != HttpVersion.Version20)
            throw new InvalidDataException($"CF /api downgraded to {response.Version}");
        int status = (int)response.StatusCode;
        if (status is 403 or 404 or 429 or 444)
            throw new MtProtoTransportException(-status, $"HTTP {status} cf_ray={cfRay}");
        if (status != 200)
            throw new HttpRequestException($"CF /api HTTP {status} cf_ray={cfRay}");
        string? contentType = response.Content.Headers.ContentType?.MediaType;
        if (contentType is not null && contentType.Contains("text/html", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("CF /api returned HTML instead of MTProto");

        long? declared = response.Content.Headers.ContentLength;
        if (declared is > PacketCodec.MaxPacket)
            throw new InvalidDataException("H2 response Content-Length exceeds native packet bound");

        await using Stream stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        var content = new MemoryStream();
        var buffer = new byte[16 * 1024];
        while (true)
        {
            int read = await stream.ReadAsync(buffer, ct).ConfigureAwait(false);
            if (read == 0) break;
            if (content.Length + read > PacketCodec.MaxPacket)
                throw new InvalidDataException("H2 response exceeds native packet bound");
            content.Write(buffer, 0, read);
        }
        byte[] bytes = content.ToArray();
        if (declared is not null && bytes.Length != declared)
            throw new InvalidDataException("incomplete H2 response body");
        if (bytes.Length != 0 && bytes.Length % 4 != 0)
            throw new InvalidDataException("CF /api response is not an aligned MTProto packet");
        if (bytes.Length == 4)
        {
            int code = BinaryPrimitives.ReadInt32LittleEndian(bytes);
            if (code < 0) throw new MtProtoTransportException(code, "HTTP 200 payload");
        }
        _log.Debug($"H2 lane={LaneId} channel={channelId} request bytes={bytes.Length}");
        return bytes;
    }

    public async ValueTask DisposeAsync() => _client.Dispose();
}

/// <summary>
/// One client's media session over HTTP/2: up to eight requests in flight, a bounded reply queue, and the one
/// MTProto packet per request rule. A request that fails with an endpoint error makes the native side receive the
/// same negative code it would have got from Telegram over a direct connection.
/// </summary>
public sealed class CfH2Channel
{
    private const int MaxChannelRequests = 8;
    private const int MaxChannelBytes = 8 * 1024 * 1024;

    private readonly CfH2Lane _lane;
    private readonly EngineLog _log;
    private readonly EngineStats _stats;
    private readonly SemaphoreSlim _slots = new(MaxChannelRequests, MaxChannelRequests);
    private readonly SemaphoreSlim _room = new(0, 1);
    private readonly object _bytesGate = new();
    private readonly Channel<object> _replies = Channel.CreateBounded<object>(new BoundedChannelOptions(16)
    {
        SingleReader = true,
        SingleWriter = false,
        FullMode = BoundedChannelFullMode.Wait,
    });
    private readonly CancellationTokenSource _requests = new();
    private long _pendingBytes;
    private volatile bool _closed;

    public int Id { get; }
    public string Label { get; }
    public int? TransportError { get; private set; }
    public bool Closed => _closed;
    public CancellationToken RequestToken => _requests.Token;

    internal CfH2Channel(CfH2Lane lane, int id, string label, EngineLog log, EngineStats stats)
    {
        _lane = lane;
        Id = id;
        Label = label;
        _log = log;
        _stats = stats;
    }

    public async Task SendAsync(byte[] body, bool quick, CancellationToken ct)
    {
        if (_closed) throw new IOException("H2 channel closed");

        while (true)
        {
            await _slots.WaitAsync(ct).ConfigureAwait(false);
            lock (_bytesGate)
            {
                if (_pendingBytes + body.Length <= MaxChannelBytes || _pendingBytes == 0)
                {
                    _pendingBytes += body.Length;
                    break;
                }
            }
            _slots.Release();
            await _room.WaitAsync(ct).ConfigureAwait(false);
        }

        _ = RunRequestAsync(body, quick);
    }

    private async Task RunRequestAsync(byte[] body, bool quick)
    {
        int length = body.Length;
        try
        {
            byte[] reply = await _lane.PostAsync(body, Id, _requests.Token).ConfigureAwait(false);
            if (!_closed && reply.Length > 0) await _replies.Writer.WriteAsync(reply).ConfigureAwait(false);
            _ = quick;
        }
        catch (MtProtoTransportException ex)
        {
            _stats.H2Error();
            if (!_closed)
            {
                _log.Warn($"[{Label}] H2 lane={_lane.LaneId} channel={Id} host={_lane.Host} " +
                          $"MTProto transport error={ex.Message}; reporting to client");
                Fail(ex);
            }
        }
        catch (OperationCanceledException)
        {
            // A request timeout is an endpoint failure; cancellation because the channel is closing is not.
            if (!_closed)
            {
                _stats.H2Error();
                Fail(new IOException("H2 request timed out"));
            }
        }
        catch (ChannelClosedException)
        {
            // the channel closed while this reply was in flight
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or InvalidDataException
                                       or ObjectDisposedException)
        {
            _stats.H2Error();
            if (!_closed)
            {
                _log.Warn($"[{Label}] H2 lane={_lane.LaneId} channel={Id} request failed: " +
                          $"{ex.GetType().Name}: {ex.Message}; closing native channel");
                Fail(ex);
            }
        }
        finally
        {
            lock (_bytesGate)
            {
                _pendingBytes -= length;
                if (_pendingBytes <= MaxChannelBytes) TryReleaseRoom();
            }
            _slots.Release();
        }
    }

    private void TryReleaseRoom()
    {
        try { if (_room.CurrentCount == 0) _room.Release(); }
        catch (SemaphoreFullException) { }
    }

    private void Fail(Exception error)
    {
        if (_closed) return;
        if (error is MtProtoTransportException transport) TransportError = transport.Code;
        _closed = true;
        _requests.Cancel();
        _replies.Writer.TryComplete(error);
    }

    public async Task<byte[]> ReceiveAsync(CancellationToken ct)
    {
        object value = await _replies.Reader.ReadAsync(ct).ConfigureAwait(false);
        return value is Exception ex ? throw ex : (byte[])value;
    }

    public void Close()
    {
        if (_closed) return;
        _closed = true;
        _requests.Cancel();
        _replies.Writer.TryComplete();
        _stats.H2Connection();
    }

    public ValueTask DisposeAsync()
    {
        Close();
        return ValueTask.CompletedTask;
    }

    /// <summary>
    /// Client TCP ↔ HTTP/2. Upload reads whole packets; download writes each reply re-encoded for the client's
    /// protocol. A transport error is written as the four-byte negative code before the connection closes.
    /// </summary>
    public static async Task BridgeAsync(Stream client, CfH2Channel channel, CryptoContext ctx, uint tag,
        EngineLog log, CancellationToken ct)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, channel.RequestToken);
        Task upload = UploadAsync(client, channel, ctx, tag, linked.Token);
        Task download = DownloadAsync(client, channel, ctx, tag, linked.Token);
        try
        {
            await Task.WhenAny(upload, download).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is EndOfStreamException or IOException or ObjectDisposedException)
        {
            log.Debug($"[{channel.Label}] H2 channel={channel.Id} bridge ended: {ex.Message}");
        }
        finally
        {
            await linked.CancelAsync().ConfigureAwait(false);
            try { await Task.WhenAll(upload, download).ConfigureAwait(false); }
            catch (Exception ex) when (ex is OperationCanceledException or ChannelClosedException or IOException
                                           or EndOfStreamException or ObjectDisposedException)
            {
                log.Debug($"[{channel.Label}] H2 channel={channel.Id} bridge ended: {ex.GetType().Name}");
            }
            if (channel.TransportError is { } code)
            {
                try
                {
                    byte[] body = new byte[4];
                    BinaryPrimitives.WriteInt32LittleEndian(body, code);
                    byte[] wire = PacketCodec.EncodeReply(body, tag);
                    using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                    await client.WriteAsync(ctx.CltEnc.Transform(wire), timeout.Token).ConfigureAwait(false);
                    await client.FlushAsync(timeout.Token).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is IOException or ObjectDisposedException or OperationCanceledException) { }
            }
            channel.Close();
        }
    }

    private static async Task UploadAsync(Stream client, CfH2Channel channel, CryptoContext ctx, uint tag,
        CancellationToken ct)
    {
        while (true)
        {
            (byte[] body, bool quick) = await PacketCodec.ReadPacketAsync(client, ctx.CltDec, tag, ct).ConfigureAwait(false);
            await channel.SendAsync(body, quick, ct).ConfigureAwait(false);
        }
    }

    private static async Task DownloadAsync(Stream client, CfH2Channel channel, CryptoContext ctx, uint tag,
        CancellationToken ct)
    {
        while (true)
        {
            byte[] body = await channel.ReceiveAsync(ct).ConfigureAwait(false);
            byte[] wire = PacketCodec.EncodeReply(body, tag);
            byte[] encrypted = ctx.CltEnc.Transform(wire);
            await client.WriteAsync(encrypted, ct).ConfigureAwait(false);
            await client.FlushAsync(ct).ConfigureAwait(false);
        }
    }
}
