using System.Runtime.InteropServices;

namespace TgwsOne.Core.Zapret;

/// <summary>Owner and permission bits (0..07777) of a file, as the ownership rule for custom strategies needs them.</summary>
public sealed record UnixFileMeta(uint OwnerUid, int Mode);

/// <summary>The few libc calls .NET has no managed API for. Linux only: callers check the OS first.</summary>
internal static class LibC
{
    private const int AtFdCwd = -100;          // AT_FDCWD
    private const uint StatxBasicStats = 0xFFF; // STATX_BASIC_STATS | STATX_BTIME
    private const int StatxSize = 256;          // sizeof(struct statx)
    public const int SigHup = 1;
    public const int SigTerm = 15;

    [DllImport("libc", EntryPoint = "statx", SetLastError = true)]
    private static extern int StatxNative(int dirfd, [MarshalAs(UnmanagedType.LPUTF8Str)] string path, int flags, uint mask, byte[] buf);

    [DllImport("libc", EntryPoint = "geteuid")]
    private static extern uint GeteuidNative();

    [DllImport("libc", EntryPoint = "kill", SetLastError = true)]
    private static extern int KillNative(int pid, int sig);

    /// <summary>
    /// Follows symlinks (the target's owner and mode are what count). null when the file can't be examined.
    /// <c>statx</c> has the same layout on every architecture, unlike <c>struct stat</c>:
    /// uid at offset 20, mode (16 bit) at offset 28.
    /// </summary>
    public static UnixFileMeta? Stat(string path)
    {
        try
        {
            var buf = new byte[StatxSize];
            if (StatxNative(AtFdCwd, path, 0, StatxBasicStats, buf) != 0) return null;
            uint uid = BitConverter.ToUInt32(buf, 20);
            int mode = BitConverter.ToUInt16(buf, 28) & 0xFFF;
            return new UnixFileMeta(uid, mode);
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            return null;
        }
    }

    public static uint GetEffectiveUserId() => GeteuidNative();

    /// <summary>Sends a signal; false when it couldn't be delivered (no such process, not permitted).</summary>
    public static bool Kill(int pid, int signal) => KillNative(pid, signal) == 0;
}
