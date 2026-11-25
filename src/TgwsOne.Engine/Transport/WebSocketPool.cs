using System.Diagnostics;

namespace TgwsOne.Engine.Transport;

/// <summary>
/// A warm pool of WebSockets to Telegram's own endpoints, one bucket per (DC, media, test) — the port of upstream
/// <c>_WsPool</c>. A client that arrives finds a connection already opened (no DNS, TCP and TLS on the critical
/// path of its handshake), and the pool refills itself in the background and rotates connections older than two
/// minutes so one side never sees a stale idle socket.
/// </summary>
public sealed class WebSocketPool : IAsyncDisposable
{
    private const double MaxAgeSeconds = 120;
    private const double CheckIntervalSeconds = 5;
    private const double RefillBackoffInitial = 1;
    private const double RefillBackoffMax = 3600;

    private readonly EngineConfig _config;
    private readonly EngineLog _log;
    private readonly EngineStats _stats;
    private readonly object _gate = new();
    private readonly Dictionary<PoolKey, Bucket> _buckets = [];
    private readonly CancellationTokenSource _stop = new();
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

    public WebSocketPool(EngineConfig config, EngineLog log, EngineStats stats)
    {
        _config = config;
        _log = log;
        _stats = stats;
    }

    private static double Now() => Stopwatch.GetTimestamp() / (double)Stopwatch.Frequency;

    public async Task<RawWebSocket?> GetAsync(int dc, bool isMedia, bool isTestDc)
    {
        string? targetIp = _config.DcRedirects.GetValueOrDefault(dc);
        if (targetIp is null || _config.PoolSize <= 0) return null;

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

    private void ScheduleRefill(PoolKey key, string targetIp, IReadOnlyList<string> domains)
    {
        if (_config.PoolSize <= 0) return;
        Bucket bucket = BucketFor(key);
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
                if (_stop.IsCancellationRequested)
                {
                    CloseQuietly(socket);
                    continue;
                }
                lock (_gate)
                {
                    bucket.Idle.Enqueue((socket, Now()));
                    connected++;
                }
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
        foreach (string domain in domains)
        {
            bool frontFirst = _tryFrontingFirst;
            foreach (bool fronted in frontFirst ? new[] { true, false } : new[] { false, true })
            {
                try
                {
                    var timeout = TimeSpan.FromSeconds(fronted ? 7 : 8);
                    RawWebSocket ws = await RawWebSocket.ConnectAsync(
                        targetIp, domain, timeout, path,
                        sni: fronted ? "sprinthost.ru" : null,
                        secure: true, bufferSize: _config.BufferSize, ct: _stop.Token).ConfigureAwait(false);
                    _tryFrontingFirst = fronted;
                    if (fronted) _stats.Fronting();
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
public sealed class CfWorkerPool : IAsyncDisposable
{
    private const double MaxAgeSeconds = 100;
    private const double CheckIntervalSeconds = 5;
    private const int PerDcLimit = 1;
    private const double RefillBackoffInitial = 1;
    private const double RefillBackoffMax = 3600;

    private readonly EngineConfig _config;
    private readonly EngineLog _log;
    private readonly EngineStats _stats;
    private readonly object _gate = new();
    private readonly Dictionary<int, Bucket> _buckets = [];
    private readonly CancellationTokenSource _stop = new();
    private bool _disposed;

    private sealed class Bucket
    {
        public readonly Queue<(RawWebSocket Socket, double Created, string WorkerDomain)> Idle = new();
        public Task? Refilling;
        public Task? Rotating;
        public int Failures;
        public double RetryAfter;
    }

    public CfWorkerPool(EngineConfig config, EngineLog log, EngineStats stats)
    {
        _config = config;
        _log = log;
        _stats = stats;
    }

    private static double Now() => Stopwatch.GetTimestamp() / (double)Stopwatch.Frequency;

    public async Task<(RawWebSocket Socket, string WorkerDomain)?> GetAsync(
        int dc, string fallbackDst, IReadOnlyList<string> workerDomains)
    {
        if (_config.PoolSize <= 0 || workerDomains.Count == 0) return null;
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

    private void ScheduleRefill(int dc, string fallbackDst, IReadOnlyList<string> workerDomains)
    {
        if (_config.PoolSize <= 0 || workerDomains.Count == 0) return;
        Bucket bucket = BucketFor(dc);
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
                lock (_gate)
                {
                    if (!_buckets.TryGetValue(dc, out Bucket? current) || current != bucket)
                    {
                        CloseQuietly(result.Value.Socket);
                        return;
                    }
                    bucket.Idle.Enqueue((result.Value.Socket, Now(), result.Value.WorkerDomain));
                    connected++;
                }
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
        string query = $"dst={Uri.EscapeDataString(fallbackDst)}&dc={dc}";
        string path = $"/apiws?{query}";

        foreach (string workerDomain in AvailableDomains(workerDomains))
        {
            try
            {
                RawWebSocket ws = await RawWebSocket.ConnectAsync(
                    workerDomain, workerDomain, TimeSpan.FromSeconds(8), path,
                    secure: !_config.DisableSecure, bufferSize: _config.BufferSize,
                    ct: _stop.Token).ConfigureAwait(false);
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
