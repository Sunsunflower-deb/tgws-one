using System.Net.Sockets;
using System.Runtime.InteropServices;

namespace TgwsOne.Engine.Desync;

/// <summary>
/// Sends fake ClientHellos with <c>vmsplice(SPLICE_F_GIFT)</c> + <c>splice</c> and keeps the page-aligned buffers
/// alive until the connection closes. After the fake leaves the socket the same pages are overwritten with the real
/// bytes of that range; the kernel may still retransmit from them, and that retransmission is what repairs the
/// stream for the server. Releasing or reusing the pages before the close would let the fake reach the server.
/// </summary>
internal sealed class FakeSpliceSender : IDisposable
{
    /// <summary>ByeDPI's default fake TTL: far enough to leave the socket, too low to cross the path to the server.</summary>
    internal const int DefaultFakeTtl = 8;

    private readonly Socket _socket;
    private readonly ISocketTricks _tricks;
    private readonly int _systemTtl;
    private readonly List<PageAlignedBuffer> _buffers = [];
    private readonly object _gate = new();
    private bool _disposed;

    public FakeSpliceSender(Socket socket, ISocketTricks tricks, int systemTtl)
    {
        _socket = socket;
        _tricks = tricks;
        _systemTtl = systemTtl;
    }

    /// <summary>
    /// Builds a fake hello of <paramref name="real"/>.Length, sends it with <paramref name="ttl"/>, restores the
    /// system TTL and then overwrites the gift pages with <paramref name="real"/>. Returns false without sending when
    /// the length cannot form a hello or the splice primitive fails, so the caller can fall back to a plain write.
    /// </summary>
    public bool TrySend(string sni, ReadOnlyMemory<byte> real, int ttl, out string? failure)
    {
        failure = null;
        byte[] fake;
        try
        {
            fake = FakeClientHello.Build(sni, real.Length);
        }
        catch (ArgumentOutOfRangeException ex)
        {
            failure = ex.Message;
            return false;
        }

        var buffer = new PageAlignedBuffer(real.Length);
        bool keep = false;
        try
        {
            buffer.Write(fake);
            bool lowered = false;
            try
            {
                _tricks.SetTtl(_socket, ttl);
                lowered = true;
                bool sent = _tricks is LinuxSocketTricks
                    ? LinuxPageSplice.TrySend(_socket, buffer, fake.Length, out failure)
                    : _tricks.TrySendSpliced(_socket, fake);
                if (!sent)
                {
                    failure ??= "vmsplice/splice failed";
                    return false;
                }
            }
            catch (Exception ex)
            {
                // The caller has a plain-write fallback, so a primitive that throws instead of returning false must
                // not take the connection down.
                failure = $"{ex.GetType().Name}: {ex.Message}";
                return false;
            }
            finally
            {
                if (lowered) RestoreTtl();
            }

            // The pages are gifted to the kernel; overwriting them now means a retransmission carries the original.
            buffer.Write(real.Span);
            lock (_gate)
            {
                if (!_disposed)
                {
                    _buffers.Add(buffer);
                    keep = true;
                }
            }

            // Even when a concurrent Dispose already freed the buffer the fake itself was sent, so the caller must
            // not write those bytes a second time.
            return true;
        }
        finally
        {
            if (!keep) buffer.Dispose();
        }
    }

    private void RestoreTtl()
    {
        try
        {
            _tricks.SetTtl(_socket, _systemTtl);
        }
        catch (Exception)
        {
            // A failed restore is not worth losing the connection over; every later step restores this same value.
        }
    }

    /// <summary>A copy of every retained buffer, for tests that check the retransmission pages hold real bytes.</summary>
    internal byte[][] SnapshotBuffers()
    {
        lock (_gate)
        {
            var result = new byte[_buffers.Count][];
            for (int index = 0; index < _buffers.Count; index++)
            {
                PageAlignedBuffer buffer = _buffers[index];
                result[index] = new byte[buffer.Length];
                Marshal.Copy(buffer.Pointer, result[index], 0, buffer.Length);
            }
            return result;
        }
    }

    public void Dispose()
    {
        PageAlignedBuffer[] buffers;
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            buffers = [.. _buffers];
            _buffers.Clear();
        }

        foreach (PageAlignedBuffer buffer in buffers) buffer.Dispose();
    }
}
