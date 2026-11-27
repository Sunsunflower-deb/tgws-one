using System.Net.Sockets;

namespace TgwsOne.Engine.Desync;

/// <summary>
/// The platform primitives the desync layer needs beyond what a managed <see cref="Socket"/> exposes: a low TTL for
/// one flight (disorder), a fake segment sent through a splice pipe, an urgent byte (OOB), and the kernel's
/// retransmission counter. Everything here is best-effort: a failing syscall must leave the connection usable, so the
/// Try-methods return false instead of throwing. Implementations keep the failing errno in an internal
/// <c>LastError</c> field for the log.
/// </summary>
public interface ISocketTricks
{
    /// <summary>TTL / IPv6 hop limit can be read and changed on this platform.</summary>
    bool SupportsLowTtl { get; }

    /// <summary>A page-aligned buffer can be handed to the kernel with vmsplice/splice (Linux, Android).</summary>
    bool SupportsSpliceFake { get; }

    /// <summary>TCP urgent data (MSG_OOB) can be sent on this platform.</summary>
    bool SupportsUrgent { get; }

    /// <summary>
    /// The TTL the OS gives new connections, read once when the egress socket is created so a strategy can restore
    /// it afterwards. Returns -1 when the platform cannot read it.
    /// </summary>
    int GetSystemTtl(Socket socket);

    /// <summary>Sets the TTL (IPv4) or unicast hop limit (IPv6); a no-op where low TTL is unsupported.</summary>
    void SetTtl(Socket socket, int ttl);

    /// <summary>
    /// Sends <paramref name="bytes"/> through a kernel pipe (vmsplice with GIFT, then splice to the socket). Returns
    /// false and sends nothing on a syscall error. A successful implementation keeps the gifted pages out of the
    /// allocator until the connection is gone: splice can leave them referenced by skb fragments after it returns.
    /// </summary>
    bool TrySendSpliced(Socket socket, ReadOnlySpan<byte> bytes);

    /// <summary>Sends <paramref name="bytes"/> with <c>MSG_OOB</c>; POSIX marks only the last byte as urgent.</summary>
    bool TrySendUrgent(Socket socket, ReadOnlySpan<byte> bytes);

    /// <summary>
    /// The kernel's total retransmission count for the connection (TCP_INFO on Linux), or null where there is no
    /// equivalent. Diagnostics only, never the data path.
    /// </summary>
    long? ReadRetransmitCount(Socket socket);
}
