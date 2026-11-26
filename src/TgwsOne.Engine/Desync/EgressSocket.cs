using System.Net.Sockets;

namespace TgwsOne.Engine.Desync;

/// <summary>A connected egress socket together with the desync-aware stream that wraps it.</summary>
public sealed class EgressConnection : IDisposable, IAsyncDisposable
{
    internal EgressConnection(Socket socket, DesyncStream stream, int systemTtl)
    {
        Socket = socket;
        Stream = stream;
        SystemTtl = systemTtl;
    }

    /// <summary>The connected socket, for diagnostics and platform counters.</summary>
    public Socket Socket { get; }

    /// <summary>The stream an <c>SslStream</c> should wrap. It owns the socket through its inner stream.</summary>
    public DesyncStream Stream { get; }

    /// <summary>The TTL that was read once before any strategy changed it.</summary>
    public int SystemTtl { get; }

    public DesyncCapabilities Capabilities => Stream.Capabilities;

    public void Dispose() => Stream.Dispose();

    public ValueTask DisposeAsync() => Stream.DisposeAsync();
}

/// <summary>
/// Creates the outgoing socket for a desync-enabled route. The socket setup mirrors <c>RawWebSocket.ConnectAsync</c>
/// (no Nagle, the same buffer sizes); the only addition is the system TTL, read once right after the connection is
/// established so a low-TTL step can restore exactly that value instead of a guessed default.
/// </summary>
public static class EgressSocket
{
    public const int DefaultBufferSize = 256 * 1024;

    public static readonly TimeSpan DefaultConnectTimeout = TimeSpan.FromSeconds(10);

    /// <param name="host">A name or an IP address, resolved by the platform like any other connect.</param>
    /// <param name="port">The remote port.</param>
    /// <param name="strategy">Null means no desync: the returned stream is a transparent pass-through.</param>
    /// <param name="timeout">Connect timeout; the default is <see cref="DefaultConnectTimeout"/>.</param>
    /// <param name="tricks">Platform primitives; the current platform's implementation by default.</param>
    /// <param name="bufferSize">Send and receive buffer size, as in the WebSocket transport.</param>
    /// <param name="log">Sink for step failures; <c>AppLog.Write</c> or <c>EngineLog.Warn</c> both fit.</param>
    public static async Task<EgressConnection> ConnectAsync(
        string host, int port, DesyncStrategy? strategy = null,
        TimeSpan? timeout = null, ISocketTricks? tricks = null,
        int bufferSize = DefaultBufferSize, Action<string>? log = null,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(host);
        ArgumentOutOfRangeException.ThrowIfLessThan(port, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(bufferSize, 1);

        TimeSpan connectTimeout = timeout ?? DefaultConnectTimeout;
        ISocketTricks socketTricks = tricks ?? SocketTricks.ForCurrentPlatform();

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(connectTimeout);

        var socket = new Socket(SocketType.Stream, ProtocolType.Tcp);
        try
        {
            await socket.ConnectAsync(host, port, timeoutCts.Token).ConfigureAwait(false);

            // Read before anything can touch the TTL: this is the value the plan has to put back.
            int systemTtl = socketTricks.GetSystemTtl(socket);

            socket.NoDelay = true;
            try
            {
                socket.ReceiveBufferSize = bufferSize;
                socket.SendBufferSize = bufferSize;
            }
            catch (SocketException)
            {
                // The OS may cap or refuse the requested size; the default still works, as in RawWebSocket.
            }

            var stream = new DesyncStream(
                new NetworkStream(socket, ownsSocket: true), socket, socketTricks, systemTtl, strategy, log);
            return new EgressConnection(socket, stream, systemTtl);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            socket.Dispose();
            throw new TimeoutException($"egress connect to {host}:{port} timed out");
        }
        catch
        {
            socket.Dispose();
            throw;
        }
    }
}
