using System.Buffers.Binary;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Text;
using TgwsOne.Engine.Desync;

namespace TgwsOne.Engine.Transport;

public sealed class WsHandshakeException(int statusCode, string statusLine, IReadOnlyDictionary<string, string>? headers = null)
    : Exception($"HTTP {statusCode}: {statusLine}")
{
    public int StatusCode { get; } = statusCode;
    public string StatusLine { get; } = statusLine;
    public IReadOnlyDictionary<string, string> Headers { get; } = headers ?? new Dictionary<string, string>();
    public string? Location => Headers.GetValueOrDefault("location");
    public bool IsRedirect => StatusCode is 301 or 302 or 303 or 307 or 308;
}

/// <summary>
/// A raw RFC 6455 client over TCP/TLS. Telegram's WebSocket endpoint needs nothing more than binary frames, and
/// the pool needs control over SNI (domain fronting) that a general-purpose client does not expose. This is the
/// port of <c>proxy/raw_websocket.py</c>.
/// </summary>
public sealed class RawWebSocket : IAsyncDisposable
{
    private const int OpContinuation = 0x0;
    private const int OpBinary = 0x2;
    private const int OpClose = 0x8;
    private const int OpPing = 0x9;
    private const int OpPong = 0xA;
    private const int MaxMessageLength = 16 * 1024 * 1024;

    private readonly Stream _stream;
    private readonly Socket? _socket;
    private readonly ExactStreamReader _reader;
    private readonly SemaphoreSlim _sendLock = new(1, 1);
    private readonly List<byte> _fragment = [];
    private volatile bool _closed;
    private Task? _pingLoop;

    private RawWebSocket(Stream stream, Socket? socket, ExactStreamReader reader)
    {
        _stream = stream;
        _socket = socket;
        _reader = reader;
    }

    public bool IsClosed => _closed;

    /// <summary>False when the peer already closed the socket (an idle pool entry needs no ping to notice).</summary>
    public bool IsHealthy
    {
        get
        {
            if (_closed) return false;
            if (_socket is null) return true;
            try
            {
                return !_socket.Poll(0, SelectMode.SelectRead) || _socket.Available > 0;
            }
            catch (Exception ex) when (ex is SocketException or ObjectDisposedException)
            {
                return false;
            }
        }
    }

    /// <param name="host">What is connected to (an IP for the direct and fronted routes, a name otherwise).</param>
    /// <param name="domain">The HTTP Host header and the certificate name unless <paramref name="sni"/> overrides it.</param>
    /// <param name="sni">A different TLS server name (domain fronting), or null.</param>
    /// <param name="secure">HTTPS (443) or plain HTTP (80).</param>
    /// <param name="port">0 means the protocol's default (443 with TLS, 80 without); tests dial a local server.</param>
    /// <param name="route">The engine route the connect belongs to, for strategy selection.</param>
    /// <param name="egress">
    /// When null the original socket path runs unchanged (the desktop invariant). When set, the stream for the TLS
    /// layer comes from it, which is where the desync layer lives.
    /// </param>
    public static async Task<RawWebSocket> ConnectAsync(
        string host, string domain, TimeSpan timeout, string path = "/apiws",
        string? sni = null, bool secure = true, int bufferSize = 256 * 1024, int port = 0,
        CancellationToken ct = default, string route = "ws", IEgress? egress = null)
    {
        string tlsName = sni ?? domain;
        int remotePort = port != 0 ? port : secure ? 443 : 80;
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(timeout);

        Socket? socket = null;
        Stream? egressStream = null;
        EgressStage stage = EgressStage.Connect;
        try
        {
            Stream stream;
            if (egress is null)
            {
                socket = new Socket(SocketType.Stream, ProtocolType.Tcp);
                await socket.ConnectAsync(host, remotePort, timeoutCts.Token).ConfigureAwait(false);
                socket.NoDelay = true;
                try
                {
                    socket.ReceiveBufferSize = bufferSize;
                    socket.SendBufferSize = bufferSize;
                }
                catch (SocketException)
                {
                    // the OS may cap or refuse the requested size; the default still works
                }

                stream = new NetworkStream(socket, ownsSocket: false);
            }
            else
            {
                // The egress stream owns its socket, so nothing above this line has to know how the route was made.
                egressStream = await egress.ConnectAsync(host, remotePort, route, tlsName, timeout, timeoutCts.Token)
                    .ConfigureAwait(false);
                stream = egressStream;
            }

            // From here on a failure belongs to TLS, then to the HTTP upgrade; only the stage, not the exception type,
            // can tell a connect timeout from a TLS timeout.
            stage = secure ? EgressStage.Tls : EgressStage.Upgrade;
            if (secure)
            {
                var ssl = new SslStream(stream, leaveInnerStreamOpen: false, ValidateCertificate(sni is null));
                var options = new SslClientAuthenticationOptions
                {
                    TargetHost = tlsName,
                    EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13,
                    ApplicationProtocols = [SslApplicationProtocol.Http11],
                };
                await ssl.AuthenticateAsClientAsync(options, timeoutCts.Token).ConfigureAwait(false);
                stream = ssl;
            }

            stage = EgressStage.Upgrade;
            string key = Convert.ToBase64String(RandomNumberGenerator.GetBytes(16));
            string request =
                $"GET {path} HTTP/1.1\r\n" +
                $"Host: {domain}\r\n" +
                "Upgrade: websocket\r\n" +
                "Connection: Upgrade\r\n" +
                $"Sec-WebSocket-Key: {key}\r\n" +
                "Sec-WebSocket-Version: 13\r\n" +
                "Sec-WebSocket-Protocol: binary\r\n" +
                "\r\n";

            using (var writeCts = CancellationTokenSource.CreateLinkedTokenSource(ct))
            {
                writeCts.CancelAfter(timeout);
                await stream.WriteAsync(Encoding.ASCII.GetBytes(request), writeCts.Token).ConfigureAwait(false);
                await stream.FlushAsync(writeCts.Token).ConfigureAwait(false);
            }

            var reader = new ExactStreamReader(stream);
            string? firstLine;
            var responseLines = new List<string>();
            using (var readCts = CancellationTokenSource.CreateLinkedTokenSource(ct))
            {
                readCts.CancelAfter(timeout);
                while (true)
                {
                    string? line = await reader.ReadLineAsync(8192, readCts.Token).ConfigureAwait(false);
                    if (line is null or "") break;
                    responseLines.Add(line);
                }
            }

            if (responseLines.Count == 0) throw new WsHandshakeException(0, "empty response");
            firstLine = responseLines[0];
            string[] parts = firstLine.Split(' ', 3);
            int status = parts.Length >= 2 && int.TryParse(parts[1], out int code) ? code : 0;
            if (status == 101) return new RawWebSocket(stream, socket, reader);

            var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (string line in responseLines.Skip(1))
            {
                int colon = line.IndexOf(':');
                if (colon > 0) headers[line[..colon].Trim()] = line[(colon + 1)..].Trim();
            }
            throw new WsHandshakeException(status, firstLine, headers);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            socket?.Dispose();
            DisposeEgressStream(egressStream);
            throw;
        }
        catch (OperationCanceledException)
        {
            socket?.Dispose();
            DisposeEgressStream(egressStream);
            var timedOut = new TimeoutException($"WebSocket connect to {host} timed out");
            (EgressFailure failure, EgressStage failedStage) = EgressErrors.Classify(timedOut, stage);
            throw WrapEgressFailure(failure, failedStage, timedOut, egress);
        }
        catch (Exception ex)
        {
            socket?.Dispose();
            DisposeEgressStream(egressStream);
            (EgressFailure failure, EgressStage failedStage) = EgressErrors.Classify(ex, stage);
            throw WrapEgressFailure(failure, failedStage, ex, egress);
        }
    }

    /// <summary>
    /// The plain path (no <see cref="IEgress"/>) keeps its historic exceptions, because the desktop must not change.
    /// With an egress the failure is typed, so a pool or the controller can classify it without parsing messages.
    /// </summary>
    private static Exception WrapEgressFailure(EgressFailure failure, EgressStage stage, Exception inner,
                                               IEgress? egress)
    {
        if (inner is EgressException) return inner;
        if (egress is null) return inner;
        return new EgressException(failure, stage, inner);
    }

    private static void DisposeEgressStream(Stream? stream)
    {
        try
        {
            stream?.Dispose();
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or SocketException)
        {
            // The peer may already have reset the socket; the caller only needs the original failure.
        }
    }

    private static RemoteCertificateValidationCallback ValidateCertificate(bool checkHostname)
        => (_, _, _, errors) => checkHostname
            ? errors == SslPolicyErrors.None
            // Fronting: the certificate is for the site whose name we borrowed, not for the IP we dialed. Its
            // chain must still be trusted (upstream also kept CERT_REQUIRED and only turned off the name check).
            : errors is SslPolicyErrors.None or SslPolicyErrors.RemoteCertificateNameMismatch;

    public async Task SendAsync(ReadOnlyMemory<byte> data, CancellationToken ct = default)
    {
        if (_closed) throw new IOException("WebSocket closed");
        byte[] frame = BuildFrame(OpBinary, data.Span, mask: true);
        await _sendLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await _stream.WriteAsync(frame, ct).ConfigureAwait(false);
            await _stream.FlushAsync(ct).ConfigureAwait(false);
        }
        finally
        {
            _sendLock.Release();
        }
    }

    public async Task SendBatchAsync(IReadOnlyList<byte[]> parts, CancellationToken ct = default)
    {
        if (_closed) throw new IOException("WebSocket closed");
        if (parts.Count == 0) return;

        int total = 0;
        var frames = new byte[parts.Count][];
        for (int i = 0; i < parts.Count; i++)
        {
            frames[i] = BuildFrame(OpBinary, parts[i], mask: true);
            total += frames[i].Length;
        }
        var buffer = new byte[total];
        int offset = 0;
        foreach (byte[] frame in frames)
        {
            frame.CopyTo(buffer, offset);
            offset += frame.Length;
        }

        await _sendLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await _stream.WriteAsync(buffer, ct).ConfigureAwait(false);
            await _stream.FlushAsync(ct).ConfigureAwait(false);
        }
        finally
        {
            _sendLock.Release();
        }
    }

    /// <summary>The next binary message, or null when the peer closed the WebSocket.</summary>
    public async Task<byte[]?> ReceiveAsync(CancellationToken ct = default)
    {
        while (!_closed)
        {
            (int opcode, bool fin, byte[] payload) = await ReadFrameAsync(ct).ConfigureAwait(false);

            switch (opcode)
            {
                case OpClose:
                    _closed = true;
                    TryWriteCloseEcho(payload);
                    return null;
                case OpPing:
                    await TryWriteControlAsync(OpPong, payload, ct).ConfigureAwait(false);
                    continue;
                case OpPong:
                    continue;
                case OpContinuation or 0x1 or OpBinary:
                    if (fin && _fragment.Count == 0) return payload;
                    _fragment.AddRange(payload);
                    if (_fragment.Count > MaxMessageLength)
                        throw new IOException($"WS message too large: {_fragment.Count} bytes");
                    if (!fin) continue;
                    byte[] message = [.. _fragment];
                    _fragment.Clear();
                    return message;
                default:
                    continue;
            }
        }
        return null;
    }

    private async Task<(int Opcode, bool Fin, byte[] Payload)> ReadFrameAsync(CancellationToken ct)
    {
        byte[] header = await _reader.ReadExactlyAsync(2, ct).ConfigureAwait(false);
        bool fin = (header[0] & 0x80) != 0;
        int opcode = header[0] & 0x0F;
        long length = header[1] & 0x7F;

        if (length == 126)
        {
            byte[] extended = await _reader.ReadExactlyAsync(2, ct).ConfigureAwait(false);
            length = BinaryPrimitives.ReadUInt16BigEndian(extended);
        }
        else if (length == 127)
        {
            byte[] extended = await _reader.ReadExactlyAsync(8, ct).ConfigureAwait(false);
            length = (long)BinaryPrimitives.ReadUInt64BigEndian(extended);
        }
        if (length > MaxMessageLength) throw new IOException($"WS frame too large: {length} bytes");

        byte[] payload = await _reader.ReadExactlyAsync((int)length, ct).ConfigureAwait(false);
        if ((header[1] & 0x80) != 0)
        {
            byte[] mask = await _reader.ReadExactlyAsync(4, ct).ConfigureAwait(false);
            for (int i = 0; i < payload.Length; i++) payload[i] ^= mask[i & 3];
        }
        return (opcode, fin, payload);
    }

    private void TryWriteCloseEcho(byte[] payload)
    {
        try
        {
            byte[] close = BuildFrame(OpClose, payload.Length >= 2 ? payload[..2] : [], mask: true);
            if (!_sendLock.Wait(TimeSpan.FromSeconds(1))) return;
            try
            {
                _stream.Write(close);
                _stream.Flush();
            }
            finally
            {
                _sendLock.Release();
            }
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or InvalidOperationException or TimeoutException)
        {
            // the peer is gone; nothing to echo to
        }
    }

    private async Task TryWriteControlAsync(int opcode, byte[] payload, CancellationToken ct)
    {
        try
        {
            byte[] frame = BuildFrame(opcode, payload, mask: true);
            await _sendLock.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                await _stream.WriteAsync(frame, ct).ConfigureAwait(false);
                await _stream.FlushAsync(ct).ConfigureAwait(false);
            }
            finally
            {
                _sendLock.Release();
            }
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or OperationCanceledException)
        {
            // a ping we cannot answer means the connection is going away
        }
    }

    private static byte[] BuildFrame(int opcode, ReadOnlySpan<byte> data, bool mask)
    {
        int length = data.Length;
        int headerLength = length < 126 ? 2 : length < 65536 ? 4 : 10;
        if (mask) headerLength += 4;
        var frame = new byte[headerLength + length];
        frame[0] = (byte)(0x80 | opcode);

        int offset;
        if (length < 126)
        {
            frame[1] = (byte)((mask ? 0x80 : 0) | length);
            offset = 2;
        }
        else if (length < 65536)
        {
            frame[1] = (byte)((mask ? 0x80 : 0) | 126);
            BinaryPrimitives.WriteUInt16BigEndian(frame.AsSpan(2), (ushort)length);
            offset = 4;
        }
        else
        {
            frame[1] = (byte)((mask ? 0x80 : 0) | 127);
            BinaryPrimitives.WriteUInt64BigEndian(frame.AsSpan(2), (ulong)length);
            offset = 10;
        }

        if (mask)
        {
            Span<byte> maskKey = frame.AsSpan(offset, 4);
            RandomNumberGenerator.Fill(maskKey);
            offset += 4;
            for (int i = 0; i < length; i++) frame[offset + i] = (byte)(data[i] ^ maskKey[i & 3]);
        }
        else
        {
            data.CopyTo(frame.AsSpan(offset));
        }
        return frame;
    }

    /// <summary>The delay before the next keep-alive ping: the interval plus a random part, so no fixed rhythm shows.</summary>
    public static TimeSpan NextPingDelay(int intervalSeconds, int jitterSeconds, Random rng)
    {
        int jitterMs = Math.Max(0, jitterSeconds) * 1000;
        int extraMs = jitterMs == 0 ? 0 : rng.Next(jitterMs + 1);
        return TimeSpan.FromSeconds(Math.Max(1, intervalSeconds)) + TimeSpan.FromMilliseconds(extraMs);
    }

    /// <summary>
    /// Sends a WebSocket ping every interval plus a random jitter until the socket closes or <paramref name="ct"/> fires.
    /// A pooled connection waits minutes between clients; an idle socket that emits nothing for that long is a pattern of
    /// its own, and the answer to a ping shows the peer is still there without waiting for a client to notice.
    /// </summary>
    public void StartPingLoop(int intervalSeconds, int jitterSeconds, CancellationToken ct)
    {
        if (_pingLoop is not null || intervalSeconds <= 0) return;
        _pingLoop = Task.Run(() => PingLoopAsync(intervalSeconds, jitterSeconds, ct), CancellationToken.None);
    }

    private async Task PingLoopAsync(int intervalSeconds, int jitterSeconds, CancellationToken ct)
    {
        try
        {
            while (!_closed && !ct.IsCancellationRequested)
            {
                await Task.Delay(NextPingDelay(intervalSeconds, jitterSeconds, Random.Shared), ct).ConfigureAwait(false);
                if (_closed) return;
                // A payload of a random length: a fixed one would be one more constant on the wire.
                byte[] payload = new byte[Random.Shared.Next(4, 13)];
                Random.Shared.NextBytes(payload);
                await TryWriteControlAsync(OpPing, payload, ct).ConfigureAwait(false);
            }
        }
        catch (Exception ex) when (ex is OperationCanceledException or IOException or ObjectDisposedException)
        {
            // the connection is going away; the ping loop has nothing left to keep alive
        }
    }

    public async Task CloseAsync()
    {
        if (_closed)
        {
            DisposeStreams();
            return;
        }
        _closed = true;
        try
        {
            byte[] close = BuildFrame(OpClose, [], mask: true);
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            await _sendLock.WaitAsync(cts.Token).ConfigureAwait(false);
            try
            {
                await _stream.WriteAsync(close, cts.Token).ConfigureAwait(false);
                await _stream.FlushAsync(cts.Token).ConfigureAwait(false);
            }
            finally
            {
                _sendLock.Release();
            }
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or OperationCanceledException)
        {
            // closing a broken connection: the point is to let go of the socket
        }
        DisposeStreams();
    }

    /// <summary>Drops the connection without the closing handshake (pool rotation, engine shutdown).</summary>
    public void Abort()
    {
        _closed = true;
        try { _socket?.Close(); } catch (SocketException) { }
        DisposeStreams();
    }

    private void DisposeStreams()
    {
        try { _stream.Dispose(); } catch (IOException) { }
        try { _socket?.Dispose(); } catch (SocketException) { }
    }

    public async ValueTask DisposeAsync()
    {
        await CloseAsync().ConfigureAwait(false);
        _sendLock.Dispose();
    }
}
