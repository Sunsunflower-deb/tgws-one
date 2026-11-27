using System.Net.Sockets;
using System.Runtime.InteropServices;

namespace TgwsOne.Engine.Desync;

/// <summary>
/// Sends a caller-owned page-aligned buffer through <c>vmsplice(SPLICE_F_GIFT)</c> + <c>splice</c>. The generic
/// <see cref="ISocketTricks.TrySendSpliced"/> retains its gifted pages for the socket's lifetime, but it cannot let
/// the fake path overwrite them with the real bytes: a retransmission has to read from the same pages. Fake therefore
/// uses this overload with a buffer that <see cref="FakeSpliceSender"/> owns and keeps until the connection closes.
/// </summary>
internal static class LinuxPageSplice
{
    // glibc bits/fcntl-linux.h: SPLICE_F_MOVE = 1, SPLICE_F_GIFT = 8.
    private const uint SpliceFMove = 1;
    private const uint SpliceFGift = 8;

    // errno 9 (EBADF) when the socket handle is already gone.
    private const int BadFileDescriptor = 9;

    public static bool TrySend(Socket socket, PageAlignedBuffer buffer, int length, out string? error)
    {
        error = null;
        if (length == 0) return true;
        if (length > buffer.Length)
        {
            error = "the buffer is shorter than the segment";
            return false;
        }

        if (!TryAcquire(socket, out bool added, out int fd))
        {
            error = $"socket handle is not valid (errno {BadFileDescriptor})";
            return false;
        }

        try
        {
            int[] pipe = new int[2];
            if (Pipe2(pipe, 0) != 0)
            {
                error = Errno("pipe2");
                return false;
            }

            try
            {
                int offset = 0;
                while (offset < length)
                {
                    var iov = new Iovec
                    {
                        Base = IntPtr.Add(buffer.Pointer, offset),
                        Length = (nuint)(length - offset),
                    };
                    nint queued = Vmsplice(pipe[1], ref iov, 1, SpliceFGift);
                    if (queued <= 0)
                    {
                        error = Errno("vmsplice");
                        return false;
                    }

                    int remaining = (int)queued;
                    while (remaining > 0)
                    {
                        nint moved = Splice(pipe[0], IntPtr.Zero, fd, IntPtr.Zero, (nuint)remaining, SpliceFMove);
                        if (moved <= 0)
                        {
                            error = Errno("splice");
                            return false;
                        }

                        remaining -= (int)moved;
                    }

                    offset += (int)queued;
                }

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

    private static string Errno(string call) => $"{call} failed with errno {Marshal.GetLastWin32Error()}";

    private static bool TryAcquire(Socket socket, out bool added, out int fd)
    {
        added = false;
        fd = -1;
        try
        {
            socket.SafeHandle.DangerousAddRef(ref added);
        }
        catch (ObjectDisposedException)
        {
            return false;
        }

        nint handle = socket.SafeHandle.DangerousGetHandle();
        if (handle == 0 || handle == -1)
        {
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

    [DllImport("libc", EntryPoint = "pipe2", SetLastError = true)]
    private static extern int Pipe2([Out] int[] fds, int flags);

    [DllImport("libc", EntryPoint = "vmsplice", SetLastError = true)]
    private static extern nint Vmsplice(int fd, ref Iovec iov, nuint nrSegs, uint flags);

    [DllImport("libc", EntryPoint = "splice", SetLastError = true)]
    private static extern nint Splice(int fdIn, IntPtr offIn, int fdOut, IntPtr offOut, nuint length, uint flags);

    [DllImport("libc", EntryPoint = "close", SetLastError = true)]
    private static extern int Close(int fd);
}
