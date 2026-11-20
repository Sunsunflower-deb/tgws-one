using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace TgwsOne.Core.Zapret;

/// <summary>
/// Owner, permission bits (0..07777) and kind of a file, as the ownership rule for custom strategies needs them.
/// <paramref name="FileType"/> is the S_IFMT part of st_mode; the default is a regular file, which is all a caller that only
/// looks at the owner and the mode has to say.
/// </summary>
public sealed record UnixFileMeta(uint OwnerUid, int Mode, int FileType = UnixFileMeta.Regular)
{
    public const int Regular = 0x8000;   // S_IFREG
    public const int Symlink = 0xA000;   // S_IFLNK
}

/// <summary>What the elevated <c>--admin</c> helper may ask about a file it was handed. Linux only: callers check the OS first.</summary>
public static class UnixFiles
{
    /// <summary>The file itself: a symlink is not followed. null when it can't be examined.</summary>
    public static UnixFileMeta? Lstat(string path) => LibC.Lstat(path);

    /// <summary>The file behind an open handle: whatever the path was swapped for since, this is what was opened.</summary>
    public static UnixFileMeta? Fstat(SafeFileHandle handle) => LibC.Fstat(handle);
}

/// <summary>The few libc calls .NET has no managed API for. Linux only: callers check the OS first.</summary>
internal static class LibC
{
    private const int AtFdCwd = -100;           // AT_FDCWD
    private const int AtSymlinkNoFollow = 0x100; // AT_SYMLINK_NOFOLLOW
    private const int AtEmptyPath = 0x1000;      // AT_EMPTY_PATH: with an empty path the descriptor itself is examined
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
    public static UnixFileMeta? Stat(string path) => StatxAt(AtFdCwd, path, 0);

    /// <summary>Like <see cref="Stat"/>, but a symlink is reported as a symlink.</summary>
    public static UnixFileMeta? Lstat(string path) => StatxAt(AtFdCwd, path, AtSymlinkNoFollow);

    /// <summary>The file an open handle refers to.</summary>
    public static UnixFileMeta? Fstat(SafeFileHandle handle)
    {
        bool added = false;
        try
        {
            handle.DangerousAddRef(ref added);
            return StatxAt((int)handle.DangerousGetHandle(), "", AtEmptyPath);
        }
        finally
        {
            if (added) handle.DangerousRelease();
        }
    }

    private static UnixFileMeta? StatxAt(int dirfd, string path, int flags)
    {
        try
        {
            var buf = new byte[StatxSize];
            if (StatxNative(dirfd, path, flags, StatxBasicStats, buf) != 0) return null;
            uint uid = BitConverter.ToUInt32(buf, 20);
            int stMode = BitConverter.ToUInt16(buf, 28);
            return new UnixFileMeta(uid, stMode & 0xFFF, stMode & 0xF000);
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
