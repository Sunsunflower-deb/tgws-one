using System.Text;

namespace TgwsOne.Core;

/// <summary>Application log: one file, rotated to <c>.1</c> once it grows past the limit. Never throws.</summary>
public sealed class AppLog
{
    private readonly string _path;
    private readonly long _maxBytes;
    private readonly object _gate = new();

    public AppLog(string path, long maxBytes = 1024 * 1024)
    {
        _path = path;
        _maxBytes = maxBytes;
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
                if (!string.IsNullOrEmpty(dir)) PrivateDirectory.Ensure(dir);
                var fi = new FileInfo(_path);
                if (fi.Exists && fi.Length > _maxBytes)
                {
                    string old = _path + ".1";
                    try { if (File.Exists(old)) File.Delete(old); } catch { /* best effort */ }
                    try { File.Move(_path, old); } catch { /* best effort */ }
                }
                File.AppendAllText(_path, $"{now:yyyy-MM-dd HH:mm:ss}  {text}{Environment.NewLine}", new UTF8Encoding(false));
            }
            catch { /* logging must never break the caller */ }
        }
        try { Line?.Invoke($"{now:HH:mm:ss}  {text}"); } catch { /* a faulty subscriber must not break logging */ }
    }
}
