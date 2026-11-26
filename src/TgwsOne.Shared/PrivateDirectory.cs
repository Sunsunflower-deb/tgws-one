namespace TgwsOne.Core;

/// <summary>
/// Creates the directories that hold the secret (settings, logs, run state). On Linux they are 0700, and one
/// that already exists with group or other bits is tightened to 0700. Only the directory asked for is changed:
/// never an existing parent. On Windows the per-user profile ACLs already do this job.
/// </summary>
public static class PrivateDirectory
{
    private const UnixFileMode Private = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute;

    public static void Ensure(string path)
    {
        if (OperatingSystem.IsWindows())
        {
            Directory.CreateDirectory(path);
            return;
        }

        // Anything this creates (the directory, and parents that did not exist yet) is born 0700.
        Directory.CreateDirectory(path, Private);
        try
        {
            if (File.GetUnixFileMode(path) != Private)
                File.SetUnixFileMode(path, Private);
        }
        catch (UnauthorizedAccessException) { /* not ours to change (an override pointing at someone else's directory) */ }
        catch (IOException) { }
    }
}
