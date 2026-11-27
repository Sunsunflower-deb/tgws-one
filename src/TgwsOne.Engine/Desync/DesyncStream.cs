using System.Net.Sockets;

namespace TgwsOne.Engine.Desync;

/// <summary>
/// Wraps the egress <see cref="Stream"/> (a <c>NetworkStream</c> over the connected socket) and rewrites only the
/// first write when it is a ClientHello and a strategy is configured. Everything after that first flight passes
/// through untouched: the desync layer must never add, drop or reorder application bytes (plan rule 3). A step that
/// cannot be applied falls back to a plain write and is logged at a limited rate, because losing the connection is
/// worse than losing the trick (plan rule 2).
/// </summary>
public sealed class DesyncStream : Stream
{
    // ByeDPI's disorder sends the first part with the lowest useful hop count; the OS keeps the segment long enough
    // to leave the socket, but not long enough to reach the server.
    private const int LowTtlValue = 1;

    private static readonly long ErrorLogIntervalTicks = TimeSpan.TicksPerSecond * 5;

    private readonly Stream _inner;
    private readonly Socket _socket;
    private readonly ISocketTricks _tricks;
    private readonly int _systemTtl;
    private readonly FakeSpliceSender _fakeSender;
    private readonly DesyncStrategy? _strategy;
    private readonly Action<string>? _log;
    private readonly SemaphoreSlim _writeGate = new(1, 1);
    private readonly CancellationTokenSource _lifetime = new();
    private readonly CancellationToken _lifetimeToken;
    private int _firstFlightConsumed;
    private int _disposed;
    private long _lastErrorLogTicks;
    private int _suppressedErrorLogs;

    /// <param name="inner">The byte stream over the connected socket.</param>
    /// <param name="socket">The same socket, needed for the platform primitives.</param>
    /// <param name="tricks">TTL/urgent/splice primitives of the current platform.</param>
    /// <param name="systemTtl">The TTL read once when the connection was made, restored after a low-TTL segment.</param>
    /// <param name="strategy">Null or an empty strategy means every write is passed through unchanged.</param>
    /// <param name="log">Where step failures go; <c>AppLog.Write</c> or <c>EngineLog.Warn</c> both fit.</param>
    public DesyncStream(Stream inner, Socket socket, ISocketTricks tricks, int systemTtl,
                        DesyncStrategy? strategy, Action<string>? log = null)
    {
        ArgumentNullException.ThrowIfNull(inner);
        ArgumentNullException.ThrowIfNull(socket);
        ArgumentNullException.ThrowIfNull(tricks);

        _inner = inner;
        _socket = socket;
        _tricks = tricks;
        _systemTtl = systemTtl;
        _strategy = strategy;
        _log = log;
        _lifetimeToken = _lifetime.Token;
        _fakeSender = new FakeSpliceSender(socket, tricks, systemTtl);
        Capabilities =
            (tricks.SupportsLowTtl ? DesyncCapabilities.LowTtl : DesyncCapabilities.None) |
            (tricks.SupportsSpliceFake ? DesyncCapabilities.SpliceFake : DesyncCapabilities.None) |
            (tricks.SupportsUrgent ? DesyncCapabilities.Urgent : DesyncCapabilities.None);
    }

    /// <summary>The primitives this stream may use, derived from <see cref="ISocketTricks"/>.</summary>
    public DesyncCapabilities Capabilities { get; }

    /// <summary>The fake sender and its retained retransmission pages; exposed so tests can inspect them.</summary>
    internal FakeSpliceSender FakeSender => _fakeSender;

    public override bool CanRead => _inner.CanRead;
    public override bool CanSeek => false;
    public override bool CanWrite => _inner.CanWrite;
    public override long Length => throw new NotSupportedException();
    public override long Position
    {
        get => throw new NotSupportedException();
        set => throw new NotSupportedException();
    }

    public override int Read(byte[] buffer, int offset, int count) => _inner.Read(buffer, offset, count);

    public override int Read(Span<byte> buffer) => _inner.Read(buffer);

    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        => _inner.ReadAsync(buffer, offset, count, cancellationToken);

    public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        => _inner.ReadAsync(buffer, cancellationToken);

    public override void Flush() => _inner.Flush();

    public override Task FlushAsync(CancellationToken cancellationToken) => _inner.FlushAsync(cancellationToken);

    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

    public override void SetLength(long value) => throw new NotSupportedException();

    public override void Write(byte[] buffer, int offset, int count)
    {
        ArgumentNullException.ThrowIfNull(buffer);
        Write(buffer.AsSpan(offset, count));
    }

    public override void Write(ReadOnlySpan<byte> buffer)
    {
        ThrowIfDisposed();
        if (Volatile.Read(ref _firstFlightConsumed) != 0)
        {
            _inner.Write(buffer);
            return;
        }

        _writeGate.Wait();
        try
        {
            if (Volatile.Read(ref _firstFlightConsumed) != 0)
            {
                _inner.Write(buffer);
                return;
            }

            Volatile.Write(ref _firstFlightConsumed, 1);
            // The synchronous path of SslStream is rare here, so sharing the async executor is cheaper than
            // maintaining a second copy of the segment logic.
            InterceptFirstFlightAsync(buffer.ToArray(), _lifetimeToken).AsTask().GetAwaiter().GetResult();
        }
        finally
        {
            _writeGate.Release();
        }
    }

    public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        if (Volatile.Read(ref _firstFlightConsumed) != 0)
            return _inner.WriteAsync(buffer, cancellationToken);

        return WriteFirstFlightAsync(buffer, cancellationToken);
    }

    private async ValueTask WriteFirstFlightAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken)
    {
        await _writeGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (Volatile.Read(ref _firstFlightConsumed) != 0)
            {
                await _inner.WriteAsync(buffer, cancellationToken).ConfigureAwait(false);
                return;
            }

            Volatile.Write(ref _firstFlightConsumed, 1);
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _lifetimeToken);
            await InterceptFirstFlightAsync(buffer, linked.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested &&
                                                  _lifetimeToken.IsCancellationRequested)
        {
            // Disposal canceled the in-flight plan. The connection is already gone, so the cancellation is the
            // normal outcome here; surfacing it would leave a faulted task when the caller posted the write and
            // does not wait for it (rule 2: the desync layer must not leave unhandled exceptions behind).
            LogRateLimited("desync: the first flight was interrupted because the stream was disposed");
        }
        finally
        {
            _writeGate.Release();
        }
    }

    private async ValueTask InterceptFirstFlightAsync(ReadOnlyMemory<byte> firstFlight, CancellationToken ct)
    {
        // Rule 1: only a record that starts with a TLS handshake is touched. Everything else is application bytes
        // as far as this layer knows, and must go out exactly as the caller wrote it.
        if (_strategy is null || !LooksLikeClientHello(firstFlight.Span) ||
            !ClientHelloLayout.TryParse(firstFlight.Span, out ClientHelloLayout layout))
        {
            await _inner.WriteAsync(firstFlight, ct).ConfigureAwait(false);
            return;
        }

        DesyncPlan plan = DesyncPlanner.Plan(_strategy, layout, firstFlight.Span, Capabilities);
        if (plan.IsEmpty)
        {
            await _inner.WriteAsync(firstFlight, ct).ConfigureAwait(false);
            return;
        }

        // A fake segment's offset points into the buffer the planner sliced; record cuts may have rebuilt it, and
        // its real bytes are what the gift pages must hold after the fake is sent.
        ReadOnlyMemory<byte> source = firstFlight;
        foreach (DesyncSegment segment in plan.Segments)
        {
            if (segment.Mode != DesyncSegmentMode.FakeSplice) continue;
            source = PlannedFirstFlight.Recover(_strategy, layout, firstFlight, Capabilities);
            break;
        }

        await ExecutePlanAsync(plan, source, ct).ConfigureAwait(false);
    }

    private static bool LooksLikeClientHello(ReadOnlySpan<byte> flight)
        => flight.Length >= 2 && flight[0] == 0x16 && flight[1] == 0x03;

    private async ValueTask ExecutePlanAsync(DesyncPlan plan, ReadOnlyMemory<byte> source, CancellationToken ct)
    {
        foreach (DesyncSegment segment in plan.Segments)
        {
            if (segment.Delay > TimeSpan.Zero)
                await Task.Delay(segment.Delay, ct).ConfigureAwait(false);

            if (segment.Bytes.IsEmpty) continue;

            switch (segment.Mode)
            {
                case DesyncSegmentMode.LowTtl:
                    await SendLowTtlAsync(segment.Bytes, ct).ConfigureAwait(false);
                    break;

                case DesyncSegmentMode.Urgent:
                    await SendUrgentAsync(segment.Bytes, ct).ConfigureAwait(false);
                    break;

                case DesyncSegmentMode.FakeSplice:
                    await SendFakeSpliceAsync(segment, source, ct).ConfigureAwait(false);
                    break;

                default:
                    await _inner.WriteAsync(segment.Bytes, ct).ConfigureAwait(false);
                    break;
            }
        }
    }

    private async ValueTask SendLowTtlAsync(ReadOnlyMemory<byte> bytes, CancellationToken ct)
    {
        if (!_tricks.SupportsLowTtl || _systemTtl < 1)
        {
            // Without a known system TTL there is nothing to restore afterwards, so the step degrades instead of
            // leaving the connection with a TTL the caller never chose.
            LogRateLimited("desync: low TTL is unavailable; sending the segment normally");
            await _inner.WriteAsync(bytes, ct).ConfigureAwait(false);
            return;
        }

        bool lowered = TrySetTtl(LowTtlValue);
        try
        {
            await _inner.WriteAsync(bytes, ct).ConfigureAwait(false);
        }
        finally
        {
            if (lowered) TrySetTtl(_systemTtl);
        }
    }

    private async ValueTask SendUrgentAsync(ReadOnlyMemory<byte> bytes, CancellationToken ct)
    {
        bool sent = false;
        if (_tricks.SupportsUrgent)
        {
            try
            {
                sent = _tricks.TrySendUrgent(_socket, bytes.Span);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // A failed primitive must not take the connection down (rule 2).
                LogRateLimited($"desync: urgent send failed ({ex.GetType().Name}: {ex.Message}); sending normally");
            }
        }

        if (sent) return;

        LogRateLimited($"desync: urgent byte was not sent; writing {bytes.Length} byte(s) normally");
        await _inner.WriteAsync(bytes, ct).ConfigureAwait(false);
    }

    private async ValueTask SendFakeSpliceAsync(DesyncSegment segment, ReadOnlyMemory<byte> source,
                                                CancellationToken ct)
    {
        if (segment.Offset < 0 || segment.Length <= 0 || segment.Offset > source.Length - segment.Length)
        {
            // The planner produced a range the recovered buffer does not have; writing the placeholder would put
            // zeros on the wire, so the safe fallback is to leave the bytes to the normal segments around it.
            LogRateLimited($"desync: fake range {segment.Offset}+{segment.Length} is outside the first flight");
            return;
        }

        ReadOnlyMemory<byte> real = source.Slice(segment.Offset, segment.Length);
        if (!_tricks.SupportsSpliceFake || _systemTtl < 1)
        {
            LogRateLimited("desync: fake splice is unavailable; sending the segment normally");
            await _inner.WriteAsync(real, ct).ConfigureAwait(false);
            return;
        }

        string sni = string.IsNullOrWhiteSpace(segment.FakeSni) ? FakeClientHello.DefaultSni : segment.FakeSni;
        int ttl = segment.FakeTtl is > 0 and < 256 ? segment.FakeTtl.Value : FakeSpliceSender.DefaultFakeTtl;

        bool sent = _fakeSender.TrySend(sni, real, ttl, out string? failure);
        if (!sent)
        {
            LogRateLimited($"desync: fake splice failed ({failure}); sending the segment normally");
            await _inner.WriteAsync(real, ct).ConfigureAwait(false);
        }
    }

    private bool TrySetTtl(int ttl)
    {
        try
        {
            _tricks.SetTtl(_socket, ttl);
            return true;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            LogRateLimited($"desync: setting TTL {ttl} failed ({ex.GetType().Name}: {ex.Message})");
            return false;
        }
    }

    private void LogRateLimited(string message)
    {
        if (_log is null) return;

        long now = DateTime.UtcNow.Ticks;
        long last = Interlocked.Read(ref _lastErrorLogTicks);
        if (last != 0 && now - last < ErrorLogIntervalTicks)
        {
            Interlocked.Increment(ref _suppressedErrorLogs);
            return;
        }

        if (Interlocked.CompareExchange(ref _lastErrorLogTicks, now, last) != last) return;

        int suppressed = Interlocked.Exchange(ref _suppressedErrorLogs, 0);
        string line = suppressed > 0 ? $"{message} (suppressed {suppressed} similar line(s))" : message;
        try
        {
            _log(line);
        }
        catch
        {
            // A journal must never break the data path.
        }
    }

    private void ThrowIfDisposed()
    {
        if (Volatile.Read(ref _disposed) != 0) throw new ObjectDisposedException(nameof(DesyncStream));
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing && Interlocked.Exchange(ref _disposed, 1) == 0)
        {
            CancelLifetime();
            try
            {
                _inner.Dispose();
            }
            catch (Exception ex) when (ex is IOException or ObjectDisposedException or SocketException)
            {
                // Disposing a socket that the peer already reset is not an error for the caller.
            }

            // The socket is closed by now, so no retransmission can still read the gift pages.
            _fakeSender.Dispose();
        }

        base.Dispose(disposing);
    }

    public override async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 0)
        {
            CancelLifetime();
            try
            {
                await _inner.DisposeAsync().ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is IOException or ObjectDisposedException or SocketException)
            {
                // Same as the synchronous path.
            }

            _fakeSender.Dispose();
        }

        GC.SuppressFinalize(this);
    }

    private void CancelLifetime()
    {
        try
        {
            _lifetime.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // Another disposal already ran.
        }
    }
}
