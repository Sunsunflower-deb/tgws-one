using System.Net.Sockets;

namespace TgwsOne.Engine.Desync;

/// <summary>Fallback for platforms the desync layer has no primitives for: the planner sees no capabilities and the
/// connection falls back to a plain send, which is the desktop behaviour when no strategy is configured.</summary>
internal sealed class UnsupportedSocketTricks : ISocketTricks
{
    public bool SupportsLowTtl => false;
    public bool SupportsSpliceFake => false;
    public bool SupportsUrgent => false;

    public int GetSystemTtl(Socket socket) => -1;

    public void SetTtl(Socket socket, int ttl)
    {
    }

    public bool TrySendSpliced(Socket socket, ReadOnlySpan<byte> bytes) => false;

    public bool TrySendUrgent(Socket socket, ReadOnlySpan<byte> bytes) => false;

    public long? ReadRetransmitCount(Socket socket) => null;
}
