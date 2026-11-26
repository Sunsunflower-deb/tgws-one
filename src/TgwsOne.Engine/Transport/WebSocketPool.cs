using TgwsOne.Engine.Desync;
using TgwsOne.Engine.Relay;

namespace TgwsOne.Engine.Transport;

/// <summary>
/// A warm pool of WebSockets to Telegram's own endpoints, one bucket per (DC, media, test) — the port of upstream
/// <c>_WsPool</c>. A client that arrives finds a connection already opened (no DNS, TCP and TLS on the critical
/// path of its handshake), and the pool refills itself in the background and rotates connections older than two
/// minutes so one side never sees a stale idle socket.
/// </summary>
public sealed class WebSocketPool : IAsyncDisposable, IIdleConnectionPool
{
    private const double MaxAgeSeconds = 120;
    private const double CheckIntervalSeconds = 5;
    private const double RefillBackoffInitial = 1;
    private const double RefillBackoffMax = 3600;

    private readonly EngineConfig _config;
    private readonly EngineLog _log;
    private readonly EngineStats _stats;
    private readonly TimeProvider _time;
    private readonly Func<string, IReadOnlyList<string>, string, CancellationToken, Task<RawWebSocket?>>? _connector;
    private IEgress? _egress;
    private readonly object _gate = new();
    private readonly Dictionary<PoolKey, Bucket> _buckets = [];
    private readonly CancellationTokenSource _stop = new();
    private double _lastClientAt;
    private volatile bool _tryFrontingFirst;
    private bool _disposed;

    private readonly record struct PoolKey(int Dc, bool IsMedia, bool IsTestDc);

    private sealed class Bucket
    {
        public readonly Queue<(RawWebSocket Socket, double Created)> Idle = new();
        public Task? Refilling;
        public Task? Rotating;
        public int Failures;
        public double RetryAfter;
    }

    public WebSocketPool(EngineConfig config, EngineLog log, EngineStats stats, IEgress? egress = null)
        : this(config, log, stats, egress, timeProvider: null, connector: null)
    {
    }

    internal WebSocketPool(EngineConfig config, EngineLog log, EngineStats stats, IEgress? egress,
                           TimeProvider? timeProvider,
                           Func<string, IReadOnlyList<string>, string, CancellationToken, Task<RawWebSocket?>>? connector)
    {
        _config = config;
        _log = log;
        _stats = stats;
        _egress = egress;
        _time = timeProvider ?? TimeProvider.System;
        _connector = connector;
        _lastClientAt = Now();
    }

    /// <summary>Swaps the egress used by connections made from now on; already open sockets keep theirs.</summary>
    public void SetEgress(IEgress? egress) => _egress = egress;

    private double Now() => _time.GetUtcNow().UtcDateTime.Ticks / (double)TimeSpan.TicksPerSecond;

    private bool IdleExpiredLocked(double now)
        => _config.PoolIdleSeconds > 0 && now - _lastClientAt >= _config.PoolIdleSeconds;

    /// <summary>Idle entries in the pool; a test observes the drain with it.</summary>
    internal int IdleCount
    {
        get { lock (_gate) return _buckets.Values.Sum(bucket => bucket.Idle.Count); }
    }

    public async Task<RawWebSocket?> GetAsync(int dc, bool isMedia, bool isTestDc)
    {
        string? targetIp = _config.DcRedirects.GetValueOrDefault(dc);
        if (targetIp is null || _config.PoolSize <= 0) return null;

        WakeUp();

        var key = new PoolKey(dc, isMedia, isTestDc);
        IReadOnlyList<string> domains = TelegramNetwork.WsDomains(dc, isMedia);
        double now = Now();

        Bucket bucket = BucketFor(key);
        RawWebSocket? hit = null;
        while (true)
        {
            (RawWebSocket Socket, double Created) entry;
            lock (_gate)
            {
                if (bucket.Idle.Count == 0) break;
                entry = bucket.Idle.Dequeue();
            }
            if (IsStale(entry.Socket, entry.Created, now))
            {
                CloseQuietly(entry.Socket);
                continue;
            }
            _stats.PoolHit();
            _log.Debug($"WS pool hit DC{dc}{(isTestDc ? "t" : "")}{(isMedia ? "m" : "")} " +
                       $"(age={now - entry.Created:0.0}s, left={bucket.Idle.Count})");
            hit = entry.Socket;
            break;
        }

        if (hit is null)
        {
            _stats.PoolMiss();
            _log.Debug($"WS pool miss DC{dc}{(isTestDc ? "t" : "")}{(isMedia ? "m" : "")}");
        }
        ScheduleRefill(key, targetIp, domains);
        return hit;
    }

    private Bucket BucketFor(PoolKey key)
    {
        lock (_gate)
        {
            if (!_buckets.TryGetValue(key, out Bucket? bucket))
            {
                bucket = new Bucket();
                _buckets[key] = bucket;
            }
            return bucket;
        }
    }

    private bool IsStale(RawWebSocket socket, double created, double now)
        => now - created >= MaxAgeSeconds || !socket.IsHealthy;

    /// <summary>
    /// A client is back: anything left from before its absence is no longer warm, and a refill may start at once
    /// instead of waiting out the backoff the last failed attempt left behind.
    /// </summary>
    private void WakeUp()
    {
        double now = Now();
        List<RawWebSocket>? sleeping = null;
        lock (_gate)
        {
            if (IdleExpiredLocked(now))
            {
                sleeping = TakeIdleLocked();
                ResetBackoffLocked();
            }
            _lastClientAt = now;
        }
        CloseAll(sleeping);
    }

    /// <summary>Closes the free connections and stops the refill/rotation loop after a long absence.</summary>
    internal bool CloseIfIdle()
    {
        double now = Now();
        List<RawWebSocket>? sleeping;
        lock (_gate)
        {
            if (!IdleExpiredLocked(now)) return false;
            sleeping = TakeIdleLocked();
            ResetBackoffLocked();
        }
        CloseAll(sleeping);
        _log.Debug($"WS pool idle for {_config.PoolIdleSeconds}s; stopped warming {sleeping.Count} connection(s)");
        return true;
    }

    private List<RawWebSocket> TakeIdleLocked()
    {
        List<RawWebSocket> sockets = [.. _buckets.Values.SelectMany(bucket => bucket.Idle.Select(entry => entry.Socket))];
        foreach (Bucket bucket in _buckets.Values) bucket.Idle.Clear();
        return sockets;
    }

    private void ResetBackoffLocked()
    {
        foreach (Bucket bucket in _buckets.Values)
        {
            bucket.Failures = 0;
            bucket.RetryAfter = 0;
        }
    }

    private void CloseAll(List<RawWebSocket>? sockets)
    {
        if (sockets is null) return;
        foreach (RawWebSocket socket in sockets) CloseQuietly(socket);
    }

    private void ScheduleRefill(PoolKey key, string targetIp, IReadOnlyList<string> domains)
    {
        if (_config.PoolSize <= 0) return;
        Bucket bucket = BucketFor(key);
        lock (_gate)
        {
            if (IdleExpiredLocked(Now())) return;
        }
        ScheduleRotation(key, targetIp, domains);

        lock (_gate)
        {
            if (bucket.Refilling is not null || Now() < bucket.RetryAfter) return;
            bucket.Refilling = Task.Run(() => RefillAsync(key, targetIp, domains));
        }
    }

    private async Task RefillAsync(PoolKey key, string targetIp, IReadOnlyList<string> domains)
    {
        try
        {
            Bucket bucket = BucketFor(key);
            int needed;
            lock (_gate) needed = _config.PoolSize - bucket.Idle.Count;
            if (needed <= 0) return;

            string path = key.IsTestDc ? TelegramNetwork.WsPathTest : TelegramNetwork.WsPath;
            var tasks = new List<Task<RawWebSocket?>>();
            for (int i = 0; i < needed; i++)
                tasks.Add(Task.Run(() => ConnectOneAsync(targetIp, domains, path)));

            int connected = 0;
            while (tasks.Count > 0)
            {
                Task<RawWebSocket?> finished = await Task.WhenAny(tasks).ConfigureAwait(false);
                tasks.Remove(finished);
                RawWebSocket? socket;
                try { socket = await finished.ConfigureAwait(false); }
                catch (Exception ex) { _log.Debug($"WS pool connect failed: {ex.Message}"); continue; }
                if (socket is null) continue;
                double now = Now();
                bool stored;
                lock (_gate)
                {
                    stored = !_stop.IsCancellationRequested && !IdleExpiredLocked(now);
                    if (stored)
                    {
                        bucket.Idle.Enqueue((socket, now));
                        connected++;
                    }
                }
                if (!stored) CloseQuietly(socket);
            }

            lock (_gate)
            {
                if (connected > 0)
                {
                    bucket.Failures = 0;
                    bucket.RetryAfter = 0;
                }
                else
                {
                    bucket.Failures++;
                    double delay = Math.Min(RefillBackoffInitial * Math.Pow(2, Math.Min(bucket.Failures - 1, 12)),
                        RefillBackoffMax);
                    bucket.RetryAfter = Now() + delay;
                    _log.Info($"WS pool refill failed for DC{key.Dc}{(key.IsTestDc ? "t" : "")}{(key.IsMedia ? "m" : "")}, " +
                              $"retry in {delay:0}s");
                }
                _log.Debug($"WS pool refilled DC{key.Dc}{(key.IsTestDc ? "t" : "")}{(key.IsMedia ? "m" : "")}: " +
                           $"{bucket.Idle.Count} ready");
            }
        }
        finally
        {
            lock (_gate)
            {
                if (_buckets.TryGetValue(key, out Bucket? b)) b.Refilling = null;
            }
        }
    }

    private void ScheduleRotation(PoolKey key, string targetIp, IReadOnlyList<string> domains)
    {
        Bucket bucket = BucketFor(key);
        lock (_gate)
        {
            if (bucket.Rotating is not null) return;
            bucket.Rotating = Task.Run(() => RotateAsync(key, targetIp, domains));
        }
    }

    private async Task RotateAsync(PoolKey key, string targetIp, IReadOnlyList<string> domains)
    {
        try
        {
            while (!_stop.IsCancellationRequested && _config.PoolSize > 0)
            {
                if (CloseIfIdle())
                {
                    _log.Debug($"WS pool idle for {_config.PoolIdleSeconds}s; rotation stopped");
                    return;
                }

                Bucket bucket = BucketFor(key);
                double now = Now();
                var expired = new List<RawWebSocket>();
                lock (_gate)
                {
                    int count = bucket.Idle.Count;
                    for (int i = 0; i < count; i++)
                    {
                        var entry = bucket.Idle.Dequeue();
                        if (IsStale(entry.Socket, entry.Created, now)) expired.Add(entry.Socket);
                        else bucket.Idle.Enqueue(entry);
                    }
                }
                foreach (RawWebSocket socket in expired) CloseQuietly(socket);
                if (expired.Count > 0)
                    _log.Debug($"WS pool rotated DC{key.Dc}{(key.IsTestDc ? "t" : "")}{(key.IsMedia ? "m" : "")}: " +
                               $"{expired.Count} stale, {bucket.Idle.Count} ready");

                int idle;
                double retryAt;
                lock (_gate)
                {
                    idle = bucket.Idle.Count;
                    retryAt = bucket.RetryAfter;
                }
                if (idle < _config.PoolSize) ScheduleRefill(key, targetIp, domains);

                double wake = now + CheckIntervalSeconds;
                if (idle > 0)
                {
                    // The bucket itself, not the dictionary: that one changes under the lock from the client side.
                    double oldest;
                    lock (_gate) oldest = bucket.Idle.Count == 0 ? wake : bucket.Idle.Min(e => e.Created);
                    wake = Math.Min(wake, oldest + MaxAgeSeconds);
                }
                if (retryAt > now) wake = Math.Min(wake, retryAt);
                await Task.Delay(TimeSpan.FromSeconds(Math.Max(0, wake - Now())), _stop.Token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            // engine stopping
        }
        finally
        {
            lock (_gate)
            {
                if (_buckets.TryGetValue(key, out Bucket? b)) b.Rotating = null;
            }
        }
    }

    private async Task<RawWebSocket?> ConnectOneAsync(string targetIp, IReadOnlyList<string> domains, string path)
    {
        if (_connector is not null) return await _connector(targetIp, domains, path, _stop.Token).ConfigureAwait(false);

        // Fronting borrows the name of the masking site: the connection is dialed to the data centre's IP, while the
        // ClientHello says the site. Without that site there is nothing to borrow, so the plain route is the only one.
        string? frontSni = _config.FakeTlsDomain.Length > 0 ? _config.FakeTlsDomain : null;

        foreach (string domain in domains)
        {
            bool[] order = frontSni is not null && _tryFrontingFirst ? [true, false] : [false, true];
            if (frontSni is null) order = [false];
            foreach (bool fronted in order)
            {
                try
                {
                    var timeout = TimeSpan.FromSeconds(fronted ? 7 : 8);
                    RawWebSocket ws = await RawWebSocket.ConnectAsync(
                        targetIp, domain, timeout, path,
                        sni: fronted ? frontSni : null,
                        secure: true, bufferSize: _config.BufferSize, ct: _stop.Token,
                        route: fronted ? "ws-fronted" : "ws", egress: _egress).ConfigureAwait(false);
                    if (frontSni is not null) _tryFrontingFirst = fronted;
                    if (fronted) _stats.Fronting();
                    // A pooled socket sits idle between clients: pings with jitter keep it alive and unsuspicious.
                    ws.StartPingLoop(_config.PingIntervalSeconds, _config.PingJitterSeconds, _stop.Token);
                    _log.Debug($"WS pool connected {domain}{path} via {targetIp} (fronting={fronted})");
                    return ws;
                }
                catch (Exception ex) when (ex is not OperationCanceledException || !_stop.IsCancellationRequested)
                {
                    _stats.WsError();
                    _log.Debug($"WS pool connect {domain}{path} via {targetIp} (fronting={fronted}): {ex.Message}");
                }
            }
        }
        return null;
    }

    private static void CloseQuietly(RawWebSocket socket)
    {
        try { socket.Abort(); }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException) { }
    }

    public void Warmup()
    {
        foreach ((int dc, string targetIp) in _config.DcRedirects)
        {
            if (string.IsNullOrEmpty(targetIp)) continue;
            foreach (bool isMedia in new[] { false, true })
            {
                var key = new PoolKey(dc, isMedia, _config.ForceTestDc);
                ScheduleRefill(key, targetIp, TelegramNetwork.WsDomains(dc, isMedia));
            }
        }
        _log.Info($"WS pool warmup started for {_config.DcRedirects.Count} DC(s)");
    }

    /// <summary>Closes every idle socket and leaves refill/rotation running for the next client.</summary>
    public void CloseIdle()
    {
        List<RawWebSocket> sockets;
        lock (_gate) sockets = TakeIdleLocked();
        CloseAll(sockets);
        if (sockets.Count > 0) _log.Debug($"WS pool closed {sockets.Count} idle connection(s)");
    }

    /// <summary>The network underneath every connection changed: drop what is free and retry at once.</summary>
    public void NetworkChanged()
    {
        CloseIdle();
        lock (_gate) ResetBackoffLocked();
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;
        _stop.Cancel();

        List<Task> tasks = [];
        List<RawWebSocket> sockets = [];
        lock (_gate)
        {
            foreach (Bucket bucket in _buckets.Values)
            {
                if (bucket.Refilling is not null) tasks.Add(bucket.Refilling);
                if (bucket.Rotating is not null) tasks.Add(bucket.Rotating);
                sockets.AddRange(bucket.Idle.Select(e => e.Socket));
                bucket.Idle.Clear();
            }
            _buckets.Clear();
        }
        foreach (RawWebSocket socket in sockets) CloseQuietly(socket);

        try { await Task.WhenAll(tasks).WaitAsync(TimeSpan.FromSeconds(3)).ConfigureAwait(false); }
        catch (Exception ex) when (ex is TimeoutException or OperationCanceledException or AggregateException) { }
        _stop.Dispose();
    }
}

/// <summary>
/// A one-slot-per-DC pool of WebSockets through a Cloudflare Worker (<c>/apiws?dst=…&amp;dc=…</c>). Only used when
/// the user configured worker domains; it is the last resort before a plain TCP fallback to the DC address.
/// </summary>
public sealed class CfWorkerPool : IAsyncDisposable, IIdleConnectionPool
{
    private const double MaxAgeSeconds = 100;
    private const double CheckIntervalSeconds = 5;
    private const int PerDcLimit = 1;
    private const double RefillBackoffInitial = 1;
    private const double RefillBackoffMax = 3600;

    private readonly EngineConfig _config;
    private readonly EngineLog _log;
    private readonly EngineStats _stats;
    private readonly TimeProvider _time;
    private readonly Func<IReadOnlyList<string>, string, int, CancellationToken, Task<(RawWebSocket Socket, string WorkerDomain)?>>? _connector;
    private IEgress? _egress;
    private readonly object _gate = new();
    private readonly Dictionary<int, Bucket> _buckets = [];
    private readonly CancellationTokenSource _stop = new();
    private double _lastClientAt;
    private bool _disposed;

    private sealed class Bucket
    {
        public readonly Queue<(RawWebSocket Socket, double Created, string WorkerDomain)> Idle = new();
        public Task? Refilling;
        public Task? Rotating;
        public int Failures;
        public double RetryAfter;
    }

    public CfWorkerPool(EngineConfig config, EngineLog log, EngineStats stats, IEgress? egress = null)
        : this(config, log, stats, egress, timeProvider: null, connector: null)
    {
    }

    internal CfWorkerPool(EngineConfig config, EngineLog log, EngineStats stats, IEgress? egress,
                          TimeProvider? timeProvider,
                          Func<IReadOnlyList<string>, string, int, CancellationToken,
                              Task<(RawWebSocket Socket, string WorkerDomain)?>>? connector)
    {
        _config = config;
        _log = log;
        _stats = stats;
        _egress = egress;
        _time = timeProvider ?? TimeProvider.System;
        _connector = connector;
        _lastClientAt = Now();
    }

    /// <summary>Swaps the egress used for connections made from now on; already open sockets keep theirs.</summary>
    public void SetEgress(IEgress? egress) => _egress = egress;

    private double Now() => _time.GetUtcNow().UtcDateTime.Ticks / (double)TimeSpan.TicksPerSecond;

    private bool IdleExpiredLocked(double now)
        => _config.PoolIdleSeconds > 0 && now - _lastClientAt >= _config.PoolIdleSeconds;

    /// <summary>Idle entries in the pool; a test observes the drain with it.</summary>
    internal int IdleCount
    {
        get { lock (_gate) return _buckets.Values.Sum(bucket => bucket.Idle.Count); }
    }

    public async Task<(RawWebSocket Socket, string WorkerDomain)?> GetAsync(
        int dc, string fallbackDst, IReadOnlyList<string> workerDomains)
    {
        if (_config.PoolSize <= 0 || workerDomains.Count == 0) return null;
        WakeUp();
        Bucket bucket = BucketFor(dc);
        double now = Now();

        while (true)
        {
            (RawWebSocket Socket, double Created, string WorkerDomain) entry;
            lock (_gate)
            {
                if (bucket.Idle.Count == 0) break;
                entry = bucket.Idle.Dequeue();
            }
            if (now - entry.Created >= MaxAgeSeconds || !entry.Socket.IsHealthy)
            {
                CloseQuietly(entry.Socket);
                continue;
            }
            _stats.CfPoolHit();
            _log.Debug($"CF worker pool hit DC{dc} via {entry.WorkerDomain} " +
                       $"(age={now - entry.Created:0.0}s, left={bucket.Idle.Count})");
            ScheduleRefill(dc, fallbackDst, workerDomains);
            return (entry.Socket, entry.WorkerDomain);
        }

        _stats.CfPoolMiss();
        ScheduleRefill(dc, fallbackDst, workerDomains);
        return null;
    }

    private Bucket BucketFor(int dc)
    {
        lock (_gate)
        {
            if (!_buckets.TryGetValue(dc, out Bucket? bucket))
            {
                bucket = new Bucket();
                _buckets[dc] = bucket;
            }
            return bucket;
        }
    }

    private void WakeUp()
    {
        double now = Now();
        List<RawWebSocket>? sleeping = null;
        lock (_gate)
        {
            if (IdleExpiredLocked(now))
            {
                sleeping = TakeIdleLocked();
                ResetBackoffLocked();
            }
            _lastClientAt = now;
        }
        CloseAll(sleeping);
    }

    /// <summary>Closes the free connections and stops the refill/rotation loop after a long absence.</summary>
    internal bool CloseIfIdle()
    {
        double now = Now();
        List<RawWebSocket> sleeping;
        lock (_gate)
        {
            if (!IdleExpiredLocked(now)) return false;
            sleeping = TakeIdleLocked();
            ResetBackoffLocked();
        }
        CloseAll(sleeping);
        _log.Debug($"CF worker pool idle for {_config.PoolIdleSeconds}s; stopped warming {sleeping.Count} connection(s)");
        return true;
    }

    private List<RawWebSocket> TakeIdleLocked()
    {
        List<RawWebSocket> sockets = [.. _buckets.Values.SelectMany(bucket => bucket.Idle.Select(entry => entry.Socket))];
        foreach (Bucket bucket in _buckets.Values) bucket.Idle.Clear();
        return sockets;
    }

    private void ResetBackoffLocked()
    {
        foreach (Bucket bucket in _buckets.Values)
        {
            bucket.Failures = 0;
            bucket.RetryAfter = 0;
        }
    }

    private void CloseAll(List<RawWebSocket>? sockets)
    {
        if (sockets is null) return;
        foreach (RawWebSocket socket in sockets) CloseQuietly(socket);
    }

    private void ScheduleRefill(int dc, string fallbackDst, IReadOnlyList<string> workerDomains)
    {
        if (_config.PoolSize <= 0 || workerDomains.Count == 0) return;
        Bucket bucket = BucketFor(dc);
        lock (_gate)
        {
            if (IdleExpiredLocked(Now())) return;
        }
        ScheduleRotation(dc, fallbackDst, workerDomains);
        lock (_gate)
        {
            if (bucket.Refilling is not null || Now() < bucket.RetryAfter) return;
            bucket.Refilling = Task.Run(() => RefillAsync(dc, fallbackDst, workerDomains, bucket));
        }
    }

    private async Task RefillAsync(int dc, string fallbackDst, IReadOnlyList<string> workerDomains, Bucket bucket)
    {
        try
        {
            int needed;
            lock (_gate) needed = Math.Min(_config.PoolSize, PerDcLimit) - bucket.Idle.Count;
            if (needed <= 0) return;

            int connected = 0;
            for (int i = 0; i < needed; i++)
            {
                (RawWebSocket Socket, string WorkerDomain)? result =
                    await ConnectOneAsync(workerDomains, fallbackDst, dc).ConfigureAwait(false);
                if (result is null) break;
                double now = Now();
                bool stored;
                lock (_gate)
                {
                    if (!_buckets.TryGetValue(dc, out Bucket? current) || current != bucket)
                    {
                        CloseQuietly(result.Value.Socket);
                        return;
                    }
                    stored = !_stop.IsCancellationRequested && !IdleExpiredLocked(now);
                    if (stored)
                    {
                        bucket.Idle.Enqueue((result.Value.Socket, now, result.Value.WorkerDomain));
                        connected++;
                    }
                }
                if (!stored) CloseQuietly(result.Value.Socket);
            }

            lock (_gate)
            {
                if (connected > 0)
                {
                    bucket.Failures = 0;
                    bucket.RetryAfter = 0;
                }
                else
                {
                    bucket.Failures++;
                    double delay = Math.Min(RefillBackoffInitial * Math.Pow(2, Math.Min(bucket.Failures - 1, 12)),
                        RefillBackoffMax);
                    bucket.RetryAfter = Now() + delay;
                    _log.Info($"CF worker pool refill failed for DC{dc}, retry in {delay:0}s");
                }
                _log.Debug($"CF worker pool refilled DC{dc}: {bucket.Idle.Count} ready");
            }
        }
        finally
        {
            lock (_gate) bucket.Refilling = null;
        }
    }

    private void ScheduleRotation(int dc, string fallbackDst, IReadOnlyList<string> workerDomains)
    {
        Bucket bucket = BucketFor(dc);
        lock (_gate)
        {
            if (bucket.Rotating is not null) return;
            bucket.Rotating = Task.Run(() => RotateAsync(dc, fallbackDst, workerDomains, bucket));
        }
    }

    private async Task RotateAsync(int dc, string fallbackDst, IReadOnlyList<string> workerDomains, Bucket bucket)
    {
        try
        {
            while (!_stop.IsCancellationRequested && _config.PoolSize > 0)
            {
                if (CloseIfIdle())
                {
                    _log.Debug($"CF worker pool idle for {_config.PoolIdleSeconds}s; rotation stopped");
                    return;
                }

                double now = Now();
                var expired = new List<RawWebSocket>();
                lock (_gate)
                {
                    int count = bucket.Idle.Count;
                    for (int i = 0; i < count; i++)
                    {
                        var entry = bucket.Idle.Dequeue();
                        if (now - entry.Created >= MaxAgeSeconds || !entry.Socket.IsHealthy) expired.Add(entry.Socket);
                        else bucket.Idle.Enqueue(entry);
                    }
                }
                foreach (RawWebSocket socket in expired) CloseQuietly(socket);
                if (expired.Count > 0)
                    _log.Debug($"CF worker pool rotated DC{dc}: {expired.Count} stale, {bucket.Idle.Count} ready");

                int idle;
                lock (_gate) idle = bucket.Idle.Count;
                if (idle < Math.Min(_config.PoolSize, PerDcLimit)) ScheduleRefill(dc, fallbackDst, workerDomains);

                double wake = now + CheckIntervalSeconds;
                await Task.Delay(TimeSpan.FromSeconds(Math.Max(0, wake - Now())), _stop.Token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            // stopping
        }
        finally
        {
            lock (_gate) bucket.Rotating = null;
        }
    }

    private async Task<(RawWebSocket Socket, string WorkerDomain)?> ConnectOneAsync(
        IReadOnlyList<string> workerDomains, string fallbackDst, int dc)
    {
        if (_connector is not null)
            return await _connector(workerDomains, fallbackDst, dc, _stop.Token).ConfigureAwait(false);

        string query = $"dst={Uri.EscapeDataString(fallbackDst)}&dc={dc}";
        string path = $"/apiws?{query}";

        foreach (string workerDomain in AvailableDomains(workerDomains))
        {
            try
            {
                RawWebSocket ws = await RawWebSocket.ConnectAsync(
                    workerDomain, workerDomain, TimeSpan.FromSeconds(8), path,
                    secure: !_config.DisableSecure, bufferSize: _config.BufferSize,
                    ct: _stop.Token, route: "cf-worker", egress: _egress).ConfigureAwait(false);
                return (ws, workerDomain);
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !_stop.IsCancellationRequested)
            {
                _log.Debug($"CF worker pool connect DC{dc} via {workerDomain} for {fallbackDst} failed: {ex.Message}");
            }
        }
        return null;
    }

    internal IReadOnlyList<string> AvailableDomains(IReadOnlyList<string> workerDomains)
    {
        var domains = new List<string>();
        foreach (string domain in workerDomains)
        {
            if (!domains.Contains(domain)) domains.Add(domain);
        }
        for (int i = domains.Count - 1; i > 0; i--)
        {
            int j = Random.Shared.Next(i + 1);
            (domains[i], domains[j]) = (domains[j], domains[i]);
        }
        return domains;
    }

    public void Warmup()
    {
        var fallbacks = TelegramNetwork.DcDefaultIps
            .Where(pair => !_config.DcRedirects.ContainsKey(pair.Key))
            .ToDictionary(pair => pair.Key, pair => pair.Value);

        if (_config.PoolSize <= 0 || fallbacks.Count == 0 || _config.CfproxyWorkerDomains.Count == 0) return;
        foreach ((int dc, string dst) in fallbacks)
            ScheduleRefill(dc, dst, _config.CfproxyWorkerDomains);
        _log.Info($"CF worker pool warmup started for {fallbacks.Count} DC(s)");
    }

    private static void CloseQuietly(RawWebSocket socket)
    {
        try { socket.Abort(); }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException) { }
    }

    /// <summary>Closes every idle socket and leaves refill/rotation running for the next client.</summary>
    public void CloseIdle()
    {
        List<RawWebSocket> sockets;
        lock (_gate) sockets = TakeIdleLocked();
        CloseAll(sockets);
        if (sockets.Count > 0) _log.Debug($"CF worker pool closed {sockets.Count} idle connection(s)");
    }

    /// <summary>The network underneath every connection changed: drop what is free and retry at once.</summary>
    public void NetworkChanged()
    {
        CloseIdle();
        lock (_gate) ResetBackoffLocked();
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;
        _stop.Cancel();

        List<Task> tasks = [];
        List<RawWebSocket> sockets = [];
        lock (_gate)
        {
            foreach (Bucket bucket in _buckets.Values)
            {
                if (bucket.Refilling is not null) tasks.Add(bucket.Refilling);
                if (bucket.Rotating is not null) tasks.Add(bucket.Rotating);
                sockets.AddRange(bucket.Idle.Select(e => e.Socket));
                bucket.Idle.Clear();
            }
            _buckets.Clear();
        }
        foreach (RawWebSocket socket in sockets) CloseQuietly(socket);
        try { await Task.WhenAll(tasks).WaitAsync(TimeSpan.FromSeconds(3)).ConfigureAwait(false); }
        catch (Exception ex) when (ex is TimeoutException or OperationCanceledException or AggregateException) { }
        _stop.Dispose();
    }
}
