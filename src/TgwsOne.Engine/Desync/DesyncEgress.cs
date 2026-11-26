using System.Diagnostics;

namespace TgwsOne.Engine.Desync;

/// <summary>
/// The outgoing TCP/TLS transport of one route. <see cref="Transport.RawWebSocket"/> and the HTTP/2 handler ask this
/// for the stream an <c>SslStream</c> should wrap, so the desync layer is the only thing that changes about the wire;
/// everything above it (TLS, the WebSocket upgrade, HTTP/2) stays exactly the same.
/// </summary>
public interface IEgress
{
    /// <param name="host">The name or IP the socket is dialed to.</param>
    /// <param name="port">The remote port.</param>
    /// <param name="route">The engine route: <c>ws</c>, <c>ws-fronted</c>, <c>cf-worker</c>, <c>cf-proxy</c>, <c>h2</c>.</param>
    /// <param name="sni">The TLS server name on the wire, which is what a strategy resolves against.</param>
    /// <param name="timeout">Connect timeout.</param>
    /// <param name="ct">Caller cancellation.</param>
    Task<Stream> ConnectAsync(string host, int port, string route, string sni, TimeSpan timeout, CancellationToken ct);
}

/// <summary>
/// The only <see cref="IEgress"/> implementation: it asks the controller for a strategy and hands the connection to
/// <see cref="EgressSocket"/>. A null strategy (or a controller that answers null) gives a transparent stream, so an
/// engine with no controller behaves as before. The controller is consulted per connection, so a later task can swap
/// the strategy on the fly without rebuilding the pools.
/// </summary>
public sealed class DesyncEgress : IEgress
{
    private readonly Func<string, string, DesyncStrategy?> _resolve;
    private readonly IDesyncController? _controller;
    private readonly Action<string>? _log;
    private readonly ISocketTricks? _tricks;
    private readonly int _bufferSize;
    private readonly EngineStats? _stats;

    /// <param name="controller">The strategy source; <see cref="IDesyncController.Resolve"/> is called per connection.</param>
    /// <param name="log">Where step failures and the one-line attempt result go; <c>EngineLog.Warn</c> fits.</param>
    /// <param name="tricks">Platform primitives; the current platform's implementation by default.</param>
    /// <param name="bufferSize">Send and receive buffer size, as in the WebSocket transport.</param>
    /// <param name="stats">Attempt counters; null means the caller keeps no engine statistics.</param>
    public DesyncEgress(IDesyncController controller, Action<string>? log = null,
                        ISocketTricks? tricks = null, int bufferSize = EgressSocket.DefaultBufferSize,
                        EngineStats? stats = null)
    {
        ArgumentNullException.ThrowIfNull(controller);
        ArgumentOutOfRangeException.ThrowIfLessThan(bufferSize, 1);
        _resolve = controller.Resolve;
        _controller = controller;
        _log = log;
        _tricks = tricks;
        _bufferSize = bufferSize;
        _stats = stats;
    }

    /// <param name="resolve">A strategy source for tests or a controller that is not an <see cref="IDesyncController"/>.</param>
    /// <inheritdoc cref="DesyncEgress(IDesyncController, Action{string}?, ISocketTricks?, int, EngineStats?)"/>
    public DesyncEgress(Func<string, string, DesyncStrategy?> resolve, Action<string>? log = null,
                        ISocketTricks? tricks = null, int bufferSize = EgressSocket.DefaultBufferSize,
                        EngineStats? stats = null)
    {
        ArgumentNullException.ThrowIfNull(resolve);
        ArgumentOutOfRangeException.ThrowIfLessThan(bufferSize, 1);
        _resolve = resolve;
        _controller = null;
        _log = log;
        _tricks = tricks;
        _bufferSize = bufferSize;
        _stats = stats;
    }

    public async Task<Stream> ConnectAsync(string host, int port, string route, string sni, TimeSpan timeout,
                                           CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrEmpty(host);
        ArgumentOutOfRangeException.ThrowIfLessThan(port, 1);

        DesyncStrategy? strategy;
        try
        {
            strategy = _resolve(sni, route);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // A broken controller must not take the route down; the plain path is always a valid answer.
            _log?.Invoke($"desync: resolving a strategy for route={route} failed: {ex.GetType().Name}: {ex.Message}");
            strategy = null;
        }

        long started = Stopwatch.GetTimestamp();
        try
        {
            EgressConnection connection = await EgressSocket.ConnectAsync(
                host, port, strategy, timeout, _tricks, _bufferSize, _log, ct).ConfigureAwait(false);
            ReportOutcome(sni, route, host, strategy?.Id, ok: true, EgressFailure.None, started);
            return connection.Stream;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // The client went away, so this is not a judgement about the route and must not be counted as one.
            throw;
        }
        catch (EgressException ex)
        {
            ReportOutcome(sni, route, host, strategy?.Id, ok: false, ex.Failure, started);
            throw;
        }
        catch (Exception ex)
        {
            (EgressFailure failure, EgressStage stage) = EgressErrors.Classify(ex, EgressStage.Connect);
            ReportOutcome(sni, route, host, strategy?.Id, ok: false, failure, started);
            throw new EgressException(failure, stage, ex);
        }
    }

    /// <summary>
    /// One line per attempt, the counters, and the controller's feedback. Attempt results are not rate-limited: one
    /// line per connection is what makes a journal usable for judging a strategy. Step failures are the noisy part
    /// and are rate-limited inside <see cref="DesyncStream"/> instead.
    /// </summary>
    private void ReportOutcome(string sni, string route, string host, string? strategyId, bool ok,
                               EgressFailure failure, long started)
    {
        TimeSpan elapsed = Stopwatch.GetElapsedTime(started);
        _stats?.EgressAttempt();
        if (ok) _stats?.EgressSuccess();
        else _stats?.EgressFailed(failure);

        string result = ok ? "ok" : failure.ToString();
        _log?.Invoke($"EGRESS route={route} host={host} strategy={strategyId ?? "none"} result={result} " +
                     $"ms={elapsed.TotalMilliseconds:0}");

        if (_controller is null) return;
        try
        {
            _controller.Report(sni, route, strategyId, ok, failure, elapsed);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _log?.Invoke($"desync: reporting an outcome for route={route} failed: " +
                         $"{ex.GetType().Name}: {ex.Message}");
        }
    }
}
