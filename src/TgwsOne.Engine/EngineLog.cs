using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace TgwsOne.Engine;

/// <summary>
/// The engine's journal: console (which the detached host redirects to engine-console.log) and, when asked, a
/// size-rotated file — the same <c>proxy.log</c> the application's Журналы page shows. One lock: log lines from
/// many connection tasks never interleave.
/// </summary>
public sealed partial class EngineLog : IDisposable
{
    private readonly object _gate = new();
    private readonly string? _path;
    private readonly long _maxBytes;
    private readonly int _backups;
    private readonly bool _verbose;
    private readonly bool _console;
    private FileStream? _file;
    private StreamWriter? _writer;
    private bool _disposed;

    public EngineLog(bool verbose, string? path = null, double maxMb = 5, int backups = 1, bool console = true)
    {
        _verbose = verbose;
        _path = string.IsNullOrEmpty(path) ? null : path;
        _maxBytes = (long)(Math.Max(0.1, maxMb) * 1024 * 1024);
        _backups = Math.Max(1, backups);
        _console = console;
        OpenFile();
    }

    public bool IsDebug => _verbose;

    public void Debug(string message)
    {
        if (_verbose) Write("DEBUG", message);
    }

    public void Info(string message) => Write("INFO", message);

    public void Warn(string message) => Write("WARNING", message);

    public void Error(string message) => Write("ERROR", message);

    private void Write(string level, string message)
    {
        string line = $"{DateTime.Now:HH:mm:ss}  {level,-5}  {CensorDomains(message)}";
        lock (_gate)
        {
            if (_disposed) return;
            if (_console)
            {
                try { Console.Error.WriteLine(line); }
                catch (IOException) { /* the console went away; the file still gets the line */ }
            }
            if (_writer is null) return;
            try
            {
                _writer.WriteLine(line);
                _writer.Flush();
                RotateIfNeeded();
            }
            catch (Exception ex) when (ex is IOException or ObjectDisposedException or UnauthorizedAccessException)
            {
                // A journal that cannot be written must not take the proxy down.
                _writer = null;
                _file?.Dispose();
                _file = null;
            }
        }
    }

    private void OpenFile()
    {
        if (_path is null) return;
        try
        {
            string? dir = Path.GetDirectoryName(_path);
            if (!string.IsNullOrEmpty(dir)) Core.PrivateDirectory.Ensure(dir);
            _file = new FileStream(_path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete);
            _writer = new StreamWriter(_file, new UTF8Encoding(false)) { AutoFlush = false };
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _writer = null;
            _file = null;
        }
    }

    private void RotateIfNeeded()
    {
        if (_file is null || _path is null || _file.Length < _maxBytes) return;
        _writer?.Flush();
        _writer?.Dispose();
        _writer = null;
        _file = null;

        for (int i = _backups; i >= 1; i--)
        {
            string from = i == 1 ? _path : $"{_path}.{i - 1}";
            string to = $"{_path}.{i}";
            try
            {
                if (File.Exists(from)) File.Move(from, to, overwrite: true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // another process holding the old file: next rotation tries again
            }
        }
        OpenFile();
    }

    // The upstream engine hides most of a hostname in the journal (privacy when a log is pasted into a bug report);
    // telegram.org and .log names stay readable, everything else keeps its TLD and half of each label.
    [GeneratedRegex(@"(?<![\w-])(?:[a-zA-Z0-9](?:[a-zA-Z0-9-]{0,61}[a-zA-Z0-9])?\.)+[a-zA-Z]{2,}(?![\w-])",
        RegexOptions.CultureInvariant)]
    private static partial Regex DomainPattern();

    internal static string CensorDomains(string text)
    {
        try
        {
            return DomainPattern().Replace(text, match =>
            {
                string domain = match.Value;
                string normalized = domain.ToLowerInvariant().TrimEnd('.');
                if (normalized == "telegram.org" || normalized.EndsWith(".telegram.org", StringComparison.Ordinal)
                    || normalized.EndsWith(".log", StringComparison.Ordinal))
                    return domain;
                string[] parts = domain.Split('.');
                if (parts.Length < 2) return domain;
                for (int i = 0; i < parts.Length - 1; i++)
                {
                    string part = parts[i];
                    int half = part.Length / 2;
                    parts[i] = part[..half] + new string('*', part.Length - half);
                }
                return string.Join('.', parts);
            });
        }
        catch (ArgumentException)
        {
            return text;
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            try { _writer?.Dispose(); } catch (IOException) { }
            try { _file?.Dispose(); } catch (IOException) { }
            _writer = null;
            _file = null;
        }
    }
}
