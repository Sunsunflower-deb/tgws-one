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
    private readonly Action<string>? _log;
    private readonly ISocketTricks? _tricks;
    private readonly int _bufferSize;

    /// <param name="controller">The strategy source; <see cref="IDesyncController.Resolve"/> is called per connection.</param>
    /// <param name="log">Where step failures go; <c>EngineLog.Warn</c> fits.</param>
    /// <param name="tricks">Platform primitives; the current platform's implementation by default.</param>
    /// <param name="bufferSize">Send and receive buffer size, as in the WebSocket transport.</param>
    public DesyncEgress(IDesyncController controller, Action<string>? log = null,
                        ISocketTricks? tricks = null, int bufferSize = EgressSocket.DefaultBufferSize)
        : this(ResolveFrom(controller), log, tricks, bufferSize)
    {
    }

    /// <param name="resolve">A strategy source for tests or a controller that is not an <see cref="IDesyncController"/>.</param>
    /// <inheritdoc cref="DesyncEgress(IDesyncController, Action{string}?, ISocketTricks?, int)"/>
    public DesyncEgress(Func<string, string, DesyncStrategy?> resolve, Action<string>? log = null,
                        ISocketTricks? tricks = null, int bufferSize = EgressSocket.DefaultBufferSize)
    {
        ArgumentNullException.ThrowIfNull(resolve);
        ArgumentOutOfRangeException.ThrowIfLessThan(bufferSize, 1);
        _resolve = resolve;
        _log = log;
        _tricks = tricks;
        _bufferSize = bufferSize;
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

        EgressConnection connection = await EgressSocket.ConnectAsync(
            host, port, strategy, timeout, _tricks, _bufferSize, _log, ct).ConfigureAwait(false);
        return connection.Stream;
    }

    private static Func<string, string, DesyncStrategy?> ResolveFrom(IDesyncController controller)
    {
        ArgumentNullException.ThrowIfNull(controller);
        return controller.Resolve;
    }
}
