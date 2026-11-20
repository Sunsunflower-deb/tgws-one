using System.Text;

namespace TgwsOne.Core;

/// <summary>
/// Application log: one file, rotated to <c>.1</c> once it grows past the limit. Never throws. By default the folder is
/// private (0700 on Linux: the user's own logs may mention what they connect to); <c>publicRead</c> is for the bypass
/// service's logs, which every user may read (the app's Журналы page shows them): the folder is opened to 0755 and the
/// files stay 0644 whatever umask the service runs with. On Windows the service's folder ACL already says the same.
/// </summary>
public sealed class AppLog
{
    private const UnixFileMode FileRead = UnixFileMode.GroupRead | UnixFileMode.OtherRead;

    private readonly string _path;
    private readonly long _maxBytes;
    private readonly bool _publicRead;
    private readonly object _gate = new();

    public AppLog(string path, long maxBytes = 1024 * 1024, bool publicRead = false)
    {
        _path = path;
        _maxBytes = maxBytes;
        _publicRead = publicRead;
    }

    /// <summary>Raised after each write with "HH:mm:ss  text", for a UI log view.</summary>
    public event Action<string>? Line;

    public void Write(string text)
    {
        DateTime now = DateTime.Now;
        lock (_gate)
        {
            try
            {
                string? dir = Path.GetDirectoryName(_path);
                if (!string.IsNullOrEmpty(dir))
                {
                    if (_publicRead) PublicDirectory.Ensure(dir);
                    else PrivateDirectory.Ensure(dir);
                }
                var fi = new FileInfo(_path);
                if (fi.Exists && fi.Length > _maxBytes)
                {
                    string old = _path + ".1";
                    try { if (File.Exists(old)) File.Delete(old); } catch { /* best effort */ }
                    try { File.Move(_path, old); } catch { /* best effort */ }
                }
                File.AppendAllText(_path, $"{now:yyyy-MM-dd HH:mm:ss}  {text}{Environment.NewLine}", new UTF8Encoding(false));
                if (_publicRead && !OperatingSystem.IsWindows())
                {
                    UnixFileMode mode = File.GetUnixFileMode(_path);
                    if ((mode & FileRead) != FileRead) File.SetUnixFileMode(_path, mode | FileRead);   // a strict umask, or an older run
                }
            }
            catch { /* logging must never break the caller */ }
        }
        try { Line?.Invoke($"{now:HH:mm:ss}  {text}"); } catch { /* a faulty subscriber must not break logging */ }
    }
}
