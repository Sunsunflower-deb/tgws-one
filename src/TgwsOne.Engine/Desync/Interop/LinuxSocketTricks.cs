using System.Net.Sockets;
using System.Runtime.InteropServices;

namespace TgwsOne.Engine.Desync;

/// <summary>
/// Linux (and Android/bionic) primitives: TTL through <c>setsockopt</c>, fake segments through
/// <c>vmsplice(SPLICE_F_GIFT)</c> + <c>splice</c>, urgent data through <c>send(MSG_OOB)</c>, retransmissions through
/// <c>getsockopt(TCP_INFO)</c>. Values are checked against the uapi headers and match bionic on Android.
/// </summary>
internal sealed class LinuxSocketTricks : ISocketTricks
{
    // linux uapi in.h: IPPROTO_IP = 0, IPPROTO_TCP = 6, IPPROTO_IPV6 = 41; IP_TTL = 2.
    private const int IpProtoIp = 0;
    private const int IpProtoTcp = 6;
    private const int IpProtoIpv6 = 41;
    private const int IpTTL = 2;

    // linux uapi in6.h: IPV6_UNICAST_HOPS = 16.
    private const int Ipv6UnicastHops = 16;

    // linux uapi tcp.h: TCP_INFO = 11.
    private const int TcpInfo = 11;

    // glibc bits/socket.h and bionic socket.h: MSG_OOB = 1.
    private const int MsgOob = 1;

    // glibc bits/fcntl-linux.h: SPLICE_F_MOVE = 1, SPLICE_F_GIFT = 8. (2 is SPLICE_F_NONBLOCK.)
    private const uint SpliceFMove = 1;
    private const uint SpliceFGift = 8;

    // struct tcp_info (linux uapi tcp.h) starts with eight __u8 fields (state, ca_state, retransmits, probes,
    // backoff, options, wscale bits, delivery/fastopen bits) = 8 bytes, then a run of __u32. tcpi_retrans is at
    // offset 36 and tcpi_total_retrans at offset 100; the leading layout is the same on 32- and 64-bit Linux and on
    // bionic because no field before them is a long or a pointer.
    private const int TcpInfoTotalRetransOffset = 100;
    private const int TcpInfoMinLength = TcpInfoTotalRetransOffset + sizeof(uint);

    // errno 9 (EBADF) when the socket handle is already gone.
    private const int BadFileDescriptor = 9;

    public bool SupportsLowTtl => true;
    public bool SupportsSpliceFake => true;
    public bool SupportsUrgent => true;

    /// <summary>The errno of the last failed syscall, or 0 after a success. Read by the caller for the log.</summary>
    internal int LastError { get; private set; }

    public int GetSystemTtl(Socket socket)
    {
        if (!TryAcquire(socket, out bool added, out int fd)) return -1;
        try
        {
            (int level, int name) = TtlOption(socket);
            uint length = sizeof(int);
            if (GetSockOptInt(fd, level, name, out int value, ref length) != 0)
            {
                LastError = Marshal.GetLastWin32Error();
                return -1;
            }

            LastError = 0;
            return value;
        }
        finally
        {
            if (added) socket.SafeHandle.DangerousRelease();
        }
    }

    public void SetTtl(Socket socket, int ttl)
    {
        if (!TryAcquire(socket, out bool added, out int fd)) return;
        try
        {
            (int level, int name) = TtlOption(socket);
            LastError = SetSockOptInt(fd, level, name, ref ttl, sizeof(int)) == 0 ? 0 : Marshal.GetLastWin32Error();
        }
        finally
        {
            if (added) socket.SafeHandle.DangerousRelease();
        }
    }

    public bool TrySendSpliced(Socket socket, ReadOnlySpan<byte> bytes)
    {
        if (bytes.IsEmpty) return true;
        if (!TryAcquire(socket, out bool added, out int fd)) return false;
        try
        {
            int[] pipe = new int[2];
            if (Pipe2(pipe, 0) != 0)
            {
                LastError = Marshal.GetLastWin32Error();
                return false;
            }

            try
            {
                using var buffer = new PageAlignedBuffer(bytes.Length);
                buffer.Write(bytes);

                int offset = 0;
                while (offset < bytes.Length)
                {
                    var iov = new Iovec
                    {
                        Base = IntPtr.Add(buffer.Pointer, offset),
                        Length = (nuint)(bytes.Length - offset),
                    };
                    nint queued = Vmsplice(pipe[1], ref iov, 1, SpliceFGift);
                    if (queued <= 0)
                    {
                        LastError = Marshal.GetLastWin32Error();
                        return false;
                    }

                    int remaining = (int)queued;
                    while (remaining > 0)
                    {
                        nint moved = Splice(pipe[0], IntPtr.Zero, fd, IntPtr.Zero, (nuint)remaining, SpliceFMove);
                        if (moved <= 0)
                        {
                            LastError = Marshal.GetLastWin32Error();
                            return false;
                        }

                        remaining -= (int)moved;
                    }

                    offset += (int)queued;
                }

                LastError = 0;
                return true;
            }
            finally
            {
                Close(pipe[0]);
                Close(pipe[1]);
            }
        }
        finally
        {
            if (added) socket.SafeHandle.DangerousRelease();
        }
    }

    public bool TrySendUrgent(Socket socket, ReadOnlySpan<byte> bytes)
    {
        if (bytes.IsEmpty) return true;
        if (!TryAcquire(socket, out bool added, out int fd)) return false;
        try
        {
            byte[] data = bytes.ToArray();
            nint sent = Send(fd, data, (nuint)data.Length, MsgOob);
            if (sent != data.Length)
            {
                LastError = sent < 0 ? Marshal.GetLastWin32Error() : 0;
                return false;
            }

            LastError = 0;
            return true;
        }
        finally
        {
            if (added) socket.SafeHandle.DangerousRelease();
        }
    }

    public long? ReadRetransmitCount(Socket socket)
    {
        if (!TryAcquire(socket, out bool added, out int fd)) return null;
        try
        {
            byte[] info = new byte[256];
            uint length = (uint)info.Length;
            if (GetSockOptBuffer(fd, IpProtoTcp, TcpInfo, info, ref length) != 0)
            {
                LastError = Marshal.GetLastWin32Error();
                return null;
            }

            if (length < TcpInfoMinLength)
            {
                LastError = 0;
                return null;
            }

            LastError = 0;
            return MemoryMarshal.Read<uint>(info.AsSpan(TcpInfoTotalRetransOffset));
        }
        finally
        {
            if (added) socket.SafeHandle.DangerousRelease();
        }
    }

    private static (int Level, int Name) TtlOption(Socket socket)
        => socket.AddressFamily == AddressFamily.InterNetworkV6
            ? (IpProtoIpv6, Ipv6UnicastHops)
            : (IpProtoIp, IpTTL);

    private bool TryAcquire(Socket socket, out bool added, out int fd)
    {
        added = false;
        fd = -1;
        try
        {
            socket.SafeHandle.DangerousAddRef(ref added);
        }
        catch (ObjectDisposedException)
        {
            LastError = BadFileDescriptor;
            return false;
        }

        nint handle = socket.SafeHandle.DangerousGetHandle();
        if (handle == 0 || handle == -1)
        {
            LastError = BadFileDescriptor;
            if (added) socket.SafeHandle.DangerousRelease();
            added = false;
            return false;
        }

        fd = (int)handle;
        return true;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Iovec
    {
        public IntPtr Base;
        public nuint Length;
    }

    [DllImport("libc", EntryPoint = "setsockopt", SetLastError = true)]
    private static extern int SetSockOptInt(int fd, int level, int name, ref int value, uint length);

    [DllImport("libc", EntryPoint = "getsockopt", SetLastError = true)]
    private static extern int GetSockOptInt(int fd, int level, int name, out int value, ref uint length);

    [DllImport("libc", EntryPoint = "getsockopt", SetLastError = true)]
    private static extern int GetSockOptBuffer(int fd, int level, int name, [Out] byte[] value, ref uint length);

    [DllImport("libc", EntryPoint = "pipe2", SetLastError = true)]
    private static extern int Pipe2([Out] int[] fds, int flags);

    [DllImport("libc", EntryPoint = "vmsplice", SetLastError = true)]
    private static extern nint Vmsplice(int fd, ref Iovec iov, nuint nrSegs, uint flags);

    [DllImport("libc", EntryPoint = "splice", SetLastError = true)]
    private static extern nint Splice(int fdIn, IntPtr offIn, int fdOut, IntPtr offOut, nuint length, uint flags);

    [DllImport("libc", EntryPoint = "send", SetLastError = true)]
    private static extern nint Send(int fd, byte[] buffer, nuint length, int flags);

    [DllImport("libc", EntryPoint = "close", SetLastError = true)]
    private static extern int Close(int fd);
}
