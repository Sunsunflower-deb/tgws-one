namespace TgwsOne.Core;

/// <summary>
/// The counterpart of <see cref="PrivateDirectory"/> for what the bypass service shares with nfqws2: that process drops
/// root privileges after start-up (to uid 0x7FFFFFFF) and then re-reads the list files and writes its state file, so every
/// directory on the way has to be readable and traversable by everybody. On Linux this adds the missing group/other
/// read and search bits (nothing is ever removed); on Windows it just creates the directory.
/// </summary>
public static class PublicDirectory
{
    private const UnixFileMode ReadAndSearch =
        UnixFileMode.GroupRead | UnixFileMode.GroupExecute | UnixFileMode.OtherRead | UnixFileMode.OtherExecute;

    public static void Ensure(string path)
    {
        Directory.CreateDirectory(path);
        if (OperatingSystem.IsWindows()) return;
        try
        {
            UnixFileMode mode = File.GetUnixFileMode(path);
            if ((mode & ReadAndSearch) != ReadAndSearch) File.SetUnixFileMode(path, mode | ReadAndSearch);
        }
        catch (UnauthorizedAccessException) { /* not ours to change */ }
        catch (IOException) { }
    }
}
