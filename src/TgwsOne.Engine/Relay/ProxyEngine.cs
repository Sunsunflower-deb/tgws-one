using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using TgwsOne.Engine.Crypto;
using TgwsOne.Engine.MtProto;
using TgwsOne.Engine.Transport;

namespace TgwsOne.Engine.Relay;

/// <summary>
/// The MTProto-over-WebSocket proxy itself: a listener for Telegram Desktop, the obfuscated2 handshake, a warm
/// WebSocket pool per DC, and the transport fallbacks (Cloudflare Worker, Cloudflare HTTP/2 for media, plain TCP).
/// This is the port of upstream tg-ws-proxy v1.11.4 (MIT, Flowseal). In tgws-one it runs inside the application
/// process instead of a PyInstaller binary.
/// </summary>
public sealed class ProxyEngine : IAsyncDisposable
{
    private const int ListenerBacklog = 512;
    private static readonly TimeSpan ReadTimeout = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan BadHandshakeDrainTimeout = TimeSpan.FromSeconds(10);
    private const int BadHandshakeDrainLimit = 64 * 1024;

    private readonly EngineConfig _config;
    private readonly EngineLog _log;
    private readonly EngineStats _stats = new();
    private readonly DomainBalancer _balancer = new();
    private readonly WebSocketPool _wsPool;
    private readonly CfWorkerPool _cfWorkerPool;
    private readonly CfDomainRefresher _refresher;
    private readonly CfH2Pool? _h2Pool;
    private readonly byte[] _secret;
    private readonly CancellationTokenSource _stop = new();
    private readonly ConcurrentDictionary<long, Task> _clients = new();
    private readonly object _tcpGate = new();
    private readonly Dictionary<(string, int), int> _tcpFailures = [];
    private readonly Dictionary<(string, int), double> _tcpRetryAfter = [];
    private readonly HashSet<(string, int)> _tcpConnecting = [];
    private TcpListener? _listener;
    private Task? _acceptLoop;
    private Task? _statsLoop;
    private long _clientId;
    private bool _stopped;

    public ProxyEngine(EngineConfig config, EngineLog? log = null)
    {
        _config = config;
        _log = log ?? new EngineLog(config.Verbose, config.LogFile, config.LogMaxMb, config.LogBackups);
        _secret = Convert.FromHexString(config.Secret);
        _wsPool = new WebSocketPool(config, _log, _stats);
        _cfWorkerPool = new CfWorkerPool(config, _log, _stats);
        _refresher = new CfDomainRefresher(_balancer, _log, config.CfproxyUserDomains);
        if (config.H2Enabled) _h2Pool = new CfH2Pool(config, _log, _stats, _balancer);
    }

    public bool IsRunning { get; private set; }
    public int Port { get; private set; }
    public EngineStats Stats => _stats;
    public EngineLog Log => _log;
    public EngineConfig Config => _config;

    private static double Now => Environment.TickCount64 / 1000.0;

    /// <summary>Binds the listener and starts the background work. Throws <see cref="SocketException"/> when the port is taken.</summary>
    public async Task StartAsync(CancellationToken ct)
    {
        if (IsRunning) return;
        IPAddress address = IPAddress.Parse(_config.Host);
        var listener = new TcpListener(address, _config.Port);
        listener.Start(ListenerBacklog);
        _listener = listener;
        Port = ((IPEndPoint)listener.LocalEndpoint).Port;
        IsRunning = true;

        _refresher.Start();
        LogBanner();

        _acceptLoop = Task.Run(() => AcceptLoopAsync(_stop.Token));
        _statsLoop = Task.Run(() => StatsLoopAsync(_stop.Token));

        _wsPool.Warmup();
        _cfWorkerPool.Warmup();

        await Task.CompletedTask;
        _ = ct;
    }

    private void LogBanner()
    {
        _log.Info("=".PadRight(60, '='));
        _log.Info("  Telegram MTProto WS Bridge Proxy (native)");
        _log.Info($"  Listening on   {_config.Host}:{Port}");
        _log.Info($"  Secret:        {_config.Secret}");
        if (_config.FakeTlsDomain.Length > 0)
            _log.Info($"  Fake TLS:      {_config.FakeTlsDomain} (masking only; native engine does not verify Fake TLS)");
        _log.Info("  Target DC IPs:");
        foreach ((int dc, string ip) in _config.DcRedirects.OrderBy(p => p.Key))
            _log.Info($"    DC{dc}: {ip}");
        if (_config.FallbackCfProxy)
            _log.Info($"  CF proxy:      enabled ({(_config.CfproxyUserDomains.Count > 0 ? string.Join(", ", _config.CfproxyUserDomains) : "auto")})");
        if (_config.CfproxyWorkerDomains.Count > 0)
            _log.Info($"  CF worker:     enabled ({string.Join(", ", _config.CfproxyWorkerDomains)})");
        if (_h2Pool is not null) _log.Info("  CF H2 media:   enabled");
        if (_config.DisableSecure) _log.Info("  No secure:     enabled (port 80 for CF proxy/worker)");
        _log.Info("=".PadRight(60, '='));
        _log.Info("  Connect:");
        _log.Info($"    {TelegramLink()}");
        _log.Info("=".PadRight(60, '='));
    }

    public string TelegramLink()
    {
        string linkHost = EngineConfig.GetLinkHost(_config.Host);
        return $"tg://proxy?server={Uri.EscapeDataString(linkHost)}&port={Port}&secret=dd{_config.Secret}";
    }

    private async Task AcceptLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            TcpClient client;
            try
            {
                client = await _listener!.AcceptTcpClientAsync(ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) { return; }
            catch (ObjectDisposedException) { return; }
            catch (SocketException ex)
            {
                if (ct.IsCancellationRequested) return;
                _log.Warn($"accept failed: {ex.SocketErrorCode}; retrying");
                try { await Task.Delay(200, ct).ConfigureAwait(false); }
                catch (OperationCanceledException) { return; }
                continue;
            }

            try
            {
                client.NoDelay = true;
                client.ReceiveBufferSize = _config.BufferSize;
                client.SendBufferSize = _config.BufferSize;
            }
            catch (SocketException)
            {
                // defaults still work
            }

            long id = Interlocked.Increment(ref _clientId);
            Task task = Task.Run(() => HandleClientAsync(client, id, ct));
            _clients[id] = task;
            _ = task.ContinueWith(t => _clients.TryRemove(id, out _), TaskScheduler.Default);
        }
    }

    private async Task StatsLoopAsync(CancellationToken ct)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(60));
        try
        {
            while (await timer.WaitForNextTickAsync(ct).ConfigureAwait(false))
                _log.Info($"stats: {_stats.Summary()}");
        }
        catch (OperationCanceledException)
        {
            // stopping
        }
    }

    private async Task HandleClientAsync(TcpClient client, long id, CancellationToken ct)
    {
        _stats.ConnectionOpened();
        string label = "?";
        NetworkStream stream;
        try
        {
            label = client.Client.RemoteEndPoint is IPEndPoint ep ? $"{ep.Address}:{ep.Port}" : $"?{id}";
            stream = client.GetStream();
        }
        catch (Exception ex) when (ex is IOException or SocketException or ObjectDisposedException)
        {
            _stats.ConnectionClosed();
            client.Dispose();
            return;
        }

        try
        {
            if (_config.ProxyProtocol)
            {
                string? line = await ReadLineAsync(stream, 107, ReadTimeout, ct).ConfigureAwait(false);
                if (line is null) return;
                if (line.StartsWith("PROXY ", StringComparison.Ordinal))
                {
                    string[] parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                    if (parts.Length >= 6) label = $"{parts[2]}:{parts[4]}";
                    _log.Debug($"[{label}] PROXY protocol: {line}");
                }
                else
                {
                    _log.Debug($"[{label}] expected PROXY header, got: {Truncate(line, 60)}");
                }
            }

            byte[] first = new byte[1];
            if (!await ReadExactlyAsync(stream, first, ReadTimeout, ct).ConfigureAwait(false)) return;

            List<byte> handshake = [first[0]];
            if (first[0] == 0x16 && _config.FakeTlsDomain.Length > 0)
            {
                await MaskAsync(stream, handshake, label, ct).ConfigureAwait(false);
                return;
            }

            byte[] rest = await ReadExactlyAsync(stream, MtProtoConstants.HandshakeLength - 1, ReadTimeout, ct).ConfigureAwait(false)
                ?? [];
            if (rest.Length != MtProtoConstants.HandshakeLength - 1) return;
            handshake.AddRange(rest);

            if (!Obfuscated2.TryParseClientHandshake(handshake.ToArray(), _secret,
                    out int dc, out bool isMedia, out uint protoTag, out byte[] clientPrekeyIv))
            {
                _stats.BadHandshake();
                _log.Warn($"[{label}] bad handshake (wrong secret or proto)");
                await DrainBadHandshakeAsync(stream, ct).ConfigureAwait(false);
                return;
            }

            bool isTestDc = _config.ForceTestDc || dc >= 10000;
            if (dc >= 10000)
            {
                _log.Info($"[{label}] test DC{dc} -> DC{dc - 10000}");
                dc -= 10000;
            }
            int dcIdx = isMedia ? -dc : dc;
            _log.Debug($"[{label}] handshake ok: DC{dc}{(isMedia ? " media" : "")} proto=0x{protoTag:X8}");

            byte[] relayInit = Obfuscated2.GenerateRelayInit(protoTag, dcIdx);
            using var ctx = new CryptoContext(clientPrekeyIv, _secret, relayInit);
            string mediaTag = isMedia ? " media" : "";
            string dcTag = $"DC{dc}{(isMedia ? "m" : "")}";

            RawWebSocket? ws = await _wsPool.GetAsync(dc, isMedia, isTestDc).ConfigureAwait(false);
            if (ws is not null)
            {
                _log.Info($"[{label}] DC{dc}{mediaTag} -> WS pool hit");
                _stats.WsConnection();
                MsgSplitter? splitter = TrySplitter(protoTag, label);
                try
                {
                    await ws.SendAsync(relayInit, ct).ConfigureAwait(false);
                }
                catch
                {
                    ws.Abort();
                    throw;
                }
                await RelayBridge.WebSocketAsync(stream, ws, ctx, splitter, label, dcTag, _stats, _log, ct)
                    .ConfigureAwait(false);
                return;
            }

            _log.Info($"[{label}] DC{dc}{mediaTag} WS pool unavailable -> fallback");
            MsgSplitter? fallbackSplitter = TrySplitter(protoTag, label);
            bool ok = await FallbackAsync(stream, relayInit, label, dc, isTestDc, isMedia, ctx,
                fallbackSplitter, protoTag, ct).ConfigureAwait(false);
            if (ok) _log.Info($"[{label}] DC{dc}{mediaTag} fallback closed");
            else _log.Warn($"[{label}] DC{dc}{mediaTag} no fallback available");
        }
        catch (OperationCanceledException)
        {
            _log.Debug($"[{label}] cancelled");
        }
        catch (EndOfStreamException)
        {
            _log.Debug($"[{label}] client disconnected");
        }
        catch (SocketException ex)
        {
            _log.Debug($"[{label}] connection reset ({ex.SocketErrorCode})");
        }
        catch (IOException ex)
        {
            _log.Debug($"[{label}] connection ended: {ex.Message}");
        }
        catch (Exception ex)
        {
            _log.Error($"[{label}] unexpected: {ex}");
        }
        finally
        {
            _stats.ConnectionClosed();
            try { client.Dispose(); } catch (Exception ex) when (ex is IOException or SocketException) { }
        }
    }

    private MsgSplitter? TrySplitter(uint protoTag, string label)
    {
        try
        {
            var splitter = new MsgSplitter(protoTag);
            _log.Debug($"[{label}] MsgSplitter activated for proto 0x{protoTag:X8}");
            return splitter;
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException)
        {
            return null;
        }
    }

    private async Task<bool> FallbackAsync(
        Stream client, byte[] relayInit, string label, int dc, bool isTestDc, bool isMedia,
        CryptoContext ctx, MsgSplitter? splitter, uint protoTag, CancellationToken ct)
    {
        IReadOnlyDictionary<int, string> table = isTestDc ? TelegramNetwork.DcTestIps : TelegramNetwork.DcDefaultIps;
        string? fallbackDst = _config.TcpFallbackHostOverride ?? table.GetValueOrDefault(dc);
        bool useCf = _config.FallbackCfProxy && !isTestDc;

        if (_config.CfproxyWorkerDomains.Count > 0 && fallbackDst is not null)
        {
            bool ok = await CfWorkerFallbackAsync(client, relayInit, label, dc, isTestDc, fallbackDst,
                isMedia, ctx, ct).ConfigureAwait(false);
            if (ok) return true;
        }

        if (useCf)
        {
            if (isMedia && _h2Pool is not null)
            {
                CfH2Channel? channel = null;
                try
                {
                    channel = await _h2Pool.OpenAsync(dc, label, ct).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is HttpRequestException or IOException or InvalidDataException or InvalidOperationException)
                {
                    _log.Debug($"[{label}] H2 open failed: {ex.Message}");
                }
                if (channel is not null)
                {
                    _stats.H2Connection();
                    _stats.CfProxy();
                    try
                    {
                        await CfH2Channel.BridgeAsync(client, channel, ctx, protoTag, _log, ct).ConfigureAwait(false);
                    }
                    finally
                    {
                        await channel.DisposeAsync().ConfigureAwait(false);
                    }
                    return true;
                }
            }

            bool ok = await CfProxyFallbackAsync(client, relayInit, label, dc, isMedia, ctx, splitter, ct)
                .ConfigureAwait(false);
            if (ok) return true;
        }

        if (fallbackDst is not null)
        {
            bool ok = await TcpFallbackAsync(client, relayInit, label, dc, fallbackDst, ctx, ct).ConfigureAwait(false);
            if (ok) return true;
        }
        return false;
    }

    private async Task<bool> CfWorkerFallbackAsync(
        Stream client, byte[] relayInit, string label, int dc, bool isTestDc, string fallbackDst,
        bool isMedia, CryptoContext ctx, CancellationToken ct)
    {
        string mediaTag = isMedia ? " media" : "";
        var workerDomains = _config.CfproxyWorkerDomains;
        RawWebSocket? ws = null;
        string? workerDomain = null;

        if (!isTestDc)
        {
            (RawWebSocket Socket, string WorkerDomain)? pooled =
                await _cfWorkerPool.GetAsync(dc, fallbackDst, workerDomains).ConfigureAwait(false);
            if (pooled is not null)
            {
                ws = pooled.Value.Socket;
                workerDomain = pooled.Value.WorkerDomain;
                _log.Info($"[{label}] DC{dc}{mediaTag} -> CF worker pool hit via {workerDomain} for {fallbackDst}");
            }
        }

        if (ws is null)
        {
            string path = $"/apiws?dst={Uri.EscapeDataString(fallbackDst)}&dc={dc}";
            foreach (string domain in AvailableWorkerDomains(workerDomains))
            {
                _log.Info($"[{label}] DC{dc}{mediaTag} -> trying CF worker {domain} for {fallbackDst}");
                try
                {
                    ws = await RawWebSocket.ConnectAsync(domain, domain, TimeSpan.FromSeconds(10), path,
                        secure: !_config.DisableSecure, bufferSize: _config.BufferSize, ct: ct).ConfigureAwait(false);
                    workerDomain = domain;
                    break;
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    _log.Warn($"[{label}] DC{dc}{mediaTag} CF worker {domain} failed: {ex.Message}");
                }
            }
        }

        if (ws is null) return false;
        _stats.CfProxy();
        try { await ws.SendAsync(relayInit, ct).ConfigureAwait(false); }
        catch { ws.Abort(); throw; }
        await RelayBridge.WebSocketAsync(client, ws, ctx, null, label, $"DC{dc}{(isMedia ? "m" : "")}",
            _stats, _log, ct).ConfigureAwait(false);
        return true;
    }

    private async Task<bool> CfProxyFallbackAsync(
        Stream client, byte[] relayInit, string label, int dc, bool isMedia, CryptoContext ctx,
        MsgSplitter? splitter, CancellationToken ct)
    {
        string mediaTag = isMedia ? " media" : "";
        _log.Info($"[{label}] DC{dc}{mediaTag} -> trying CF proxy");
        var started = Stopwatch.StartNew();

        RawWebSocket? ws = null;
        string? chosen = null;
        foreach (string baseDomain in _balancer.GetDomainsForDc(dc))
        {
            string host = $"kws{dc}.{baseDomain}";
            try
            {
                ws = await RawWebSocket.ConnectAsync(host, host, TimeSpan.FromSeconds(10),
                    secure: !_config.DisableSecure, bufferSize: _config.BufferSize, ct: ct).ConfigureAwait(false);
                chosen = baseDomain;
                break;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _log.Warn($"[{label}] DC{dc}{mediaTag} CF proxy failed: {ex.Message}");
            }
        }

        if (ws is null) return false;
        _log.Debug($"[{label}] WS CONNECT DC{dc}{mediaTag} route=cf setup_ms={(long)started.Elapsed.TotalMilliseconds}");
        if (chosen is not null && _balancer.UpdateDomainForDc(dc, chosen))
            _log.Info($"[{label}] Switched active CF domain");

        _stats.CfProxy();
        try { await ws.SendAsync(relayInit, ct).ConfigureAwait(false); }
        catch { ws.Abort(); throw; }
        await RelayBridge.WebSocketAsync(client, ws, ctx, splitter, label, $"DC{dc}{(isMedia ? "m" : "")}",
            _stats, _log, ct).ConfigureAwait(false);
        return true;
    }

    private async Task<bool> TcpFallbackAsync(
        Stream client, byte[] relayInit, string label, int dc, string dst, CryptoContext ctx, CancellationToken ct)
    {
        int port = _config.TcpFallbackPort;
        var key = (dst, port);
        lock (_tcpGate)
        {
            if (_tcpConnecting.Contains(key) || Now < _tcpRetryAfter.GetValueOrDefault(key))
            {
                _log.Debug($"[{label}] TCP fallback to {dst}:{port} skipped (backoff or connecting)");
                return false;
            }
            _tcpConnecting.Add(key);
        }

        var remote = new TcpClient();
        try
        {
            try
            {
                _log.Info($"[{label}] TCP fallback to {dst}:{port}");
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
                timeout.CancelAfter(TimeSpan.FromSeconds(10));
                await remote.ConnectAsync(dst, port, timeout.Token).ConfigureAwait(false);
                remote.NoDelay = true;
                remote.ReceiveBufferSize = _config.BufferSize;
                remote.SendBufferSize = _config.BufferSize;
                NetworkStream stream = remote.GetStream();
                await stream.WriteAsync(relayInit, timeout.Token).ConfigureAwait(false);
                await stream.FlushAsync(timeout.Token).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
            {
                int failures;
                double delay;
                lock (_tcpGate)
                {
                    failures = _tcpFailures.GetValueOrDefault(key) + 1;
                    _tcpFailures[key] = failures;
                    delay = Math.Min(30 * Math.Pow(2, Math.Min(failures - 1, 7)), 3600);
                    _tcpRetryAfter[key] = Now + delay;
                }
                _log.Warn($"[{label}] TCP fallback to {dst}:{port} failed: {ex.Message}; retry in {delay:0}s");
                remote.Dispose();
                return false;
            }
            finally
            {
                lock (_tcpGate) _tcpConnecting.Remove(key);
            }

            lock (_tcpGate)
            {
                _tcpFailures.Remove(key);
                _tcpRetryAfter.Remove(key);
            }
            _stats.TcpFallback();
            await RelayBridge.TcpAsync(client, remote, ctx, label, _stats, _log, ct).ConfigureAwait(false);
            return true;
        }
        finally
        {
            remote.Dispose();
        }
    }

    private IReadOnlyList<string> AvailableWorkerDomains(IReadOnlyList<string> domains)
        => _cfWorkerPool.AvailableDomains(domains);

    // ---------- reading helpers ----------

    private static async Task<byte[]?> ReadExactlyAsync(Stream stream, int count, TimeSpan timeout, CancellationToken ct)
    {
        var buffer = new byte[count];
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(timeout);
            await stream.ReadExactlyAsync(buffer, cts.Token).ConfigureAwait(false);
            return buffer;
        }
        catch (EndOfStreamException)
        {
            return null;
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return null;
        }
    }

    private static async Task<bool> ReadExactlyAsync(Stream stream, byte[] buffer, TimeSpan timeout, CancellationToken ct)
    {
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(timeout);
            await stream.ReadExactlyAsync(buffer, cts.Token).ConfigureAwait(false);
            return true;
        }
        catch (EndOfStreamException)
        {
            return false;
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return false;
        }
    }

    private static async Task<string?> ReadLineAsync(Stream stream, int maxLength, TimeSpan timeout, CancellationToken ct)
    {
        var bytes = new List<byte>();
        var one = new byte[1];
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeout);
        while (bytes.Count <= maxLength)
        {
            try
            {
                await stream.ReadExactlyAsync(one, cts.Token).ConfigureAwait(false);
            }
            catch (EndOfStreamException)
            {
                return bytes.Count == 0 ? null : System.Text.Encoding.ASCII.GetString([.. bytes]);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                return bytes.Count == 0 ? null : System.Text.Encoding.ASCII.GetString([.. bytes]);
            }
            if (one[0] == (byte)'\n') break;
            bytes.Add(one[0]);
        }
        if (bytes.Count > 0 && bytes[^1] == (byte)'\r') bytes.RemoveAt(bytes.Count - 1);
        return System.Text.Encoding.ASCII.GetString([.. bytes]).Trim();
    }

    private async Task DrainBadHandshakeAsync(Stream stream, CancellationToken ct)
    {
        int remaining = BadHandshakeDrainLimit;
        var buffer = new byte[4096];
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(BadHandshakeDrainTimeout);
            while (remaining > 0)
            {
                int read = await stream.ReadAsync(buffer.AsMemory(0, Math.Min(buffer.Length, remaining)), cts.Token)
                    .ConfigureAwait(false);
                if (read == 0) break;
                remaining -= read;
            }
        }
        catch (Exception ex) when (ex is OperationCanceledException or IOException or SocketException)
        {
            // the point is only to avoid an RST before the client has read the close
        }
    }

    // Fake TLS is not used by tgws-one (its link is dd-secret, not ee-secret). A TLS-looking first byte is
    // relayed to the masking domain verbatim, so a prober sees a real site; a real Telegram ee-client cannot be
    // validated here and is not supported by the native engine.
    private async Task MaskAsync(Stream client, List<byte> prefix, string label, CancellationToken ct)
    {
        string domain = _config.FakeTlsDomain;
        try
        {
            using var tcp = new TcpClient();
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(10));
            await tcp.ConnectAsync(domain, 443, cts.Token).ConfigureAwait(false);
            NetworkStream upstream = tcp.GetStream();
            await upstream.WriteAsync(prefix.ToArray(), cts.Token).ConfigureAwait(false);
            await upstream.FlushAsync(cts.Token).ConfigureAwait(false);
            _stats.Masked();
            _log.Debug($"[{label}] masking -> {domain}:443");
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct);
            Task up = Relay(client, upstream, linked.Token);
            Task down = Relay(upstream, client, linked.Token);
            await Task.WhenAny(up, down).ConfigureAwait(false);
            await linked.CancelAsync().ConfigureAwait(false);
            await Task.WhenAll(up, down).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or SocketException or OperationCanceledException)
        {
            _log.Debug($"[{label}] masking failed: {ex.Message}");
        }

        static async Task Relay(Stream from, Stream to, CancellationToken ct)
        {
            var buffer = new byte[16384];
            try
            {
                while (true)
                {
                    int read = await from.ReadAsync(buffer, ct).ConfigureAwait(false);
                    if (read == 0) break;
                    await to.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);
                    await to.FlushAsync(ct).ConfigureAwait(false);
                }
            }
            catch (Exception ex) when (ex is IOException or SocketException or ObjectDisposedException or OperationCanceledException) { }
        }
    }

    private static string Truncate(string s, int max) => s.Length <= max ? s : s[..max];

    public async Task StopAsync()
    {
        if (_stopped) return;
        _stopped = true;
        IsRunning = false;
        await _stop.CancelAsync().ConfigureAwait(false);
        try { _listener?.Stop(); } catch (Exception ex) when (ex is SocketException or ObjectDisposedException) { }

        Task[] tasks = [.. _clients.Values];
        try
        {
            await Task.WhenAll(tasks).WaitAsync(TimeSpan.FromSeconds(3)).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is TimeoutException or OperationCanceledException or AggregateException) { }

        await _wsPool.DisposeAsync().ConfigureAwait(false);
        await _cfWorkerPool.DisposeAsync().ConfigureAwait(false);
        if (_h2Pool is not null) await _h2Pool.DisposeAsync().ConfigureAwait(false);
        _refresher.Dispose();

        try { if (_acceptLoop is not null) await _acceptLoop.ConfigureAwait(false); } catch (OperationCanceledException) { }
        try { if (_statsLoop is not null) await _statsLoop.ConfigureAwait(false); } catch (OperationCanceledException) { }

        _log.Info($"stopped; final stats: {_stats.Summary()}");
        _stop.Dispose();
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync().ConfigureAwait(false);
        _log.Dispose();
    }
}
