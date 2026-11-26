using System.Net.Sockets;

namespace TgwsOne.Engine.Desync;

/// <summary>
/// Windows only needs the TTL primitive here: the managed <see cref="Socket.Ttl"/> maps to IP_TTL, so a low-TTL
/// flight can be sent without P/Invoke. There is no managed equivalent of vmsplice/splice or of TCP_INFO, and the
/// desktop keeps using zapret2 for real desync anyway, so fake segments, urgent data and retransmission counters are
/// reported as unsupported instead of being emulated with raw packets (SendTcpSegment and friends are not part of the
/// public socket API).
/// </summary>
internal sealed class WindowsSocketTricks : ISocketTricks
{
    public bool SupportsLowTtl => true;
    public bool SupportsSpliceFake => false;
    public bool SupportsUrgent => false;

    /// <summary>The socket error code of the last failed operation, or 0 after a success.</summary>
    internal int LastError { get; private set; }

    public int GetSystemTtl(Socket socket)
    {
        try
        {
            LastError = 0;
            return (int)socket.Ttl;
        }
        catch (SocketException ex)
        {
            LastError = ex.ErrorCode;
            return -1;
        }
    }

    public void SetTtl(Socket socket, int ttl)
    {
        try
        {
            socket.Ttl = (short)ttl;
            LastError = 0;
        }
        catch (SocketException ex)
        {
            LastError = ex.ErrorCode;
        }
    }

    public bool TrySendSpliced(Socket socket, ReadOnlySpan<byte> bytes) => false;

    public bool TrySendUrgent(Socket socket, ReadOnlySpan<byte> bytes) => false;

    public long? ReadRetransmitCount(Socket socket) => null;
}
