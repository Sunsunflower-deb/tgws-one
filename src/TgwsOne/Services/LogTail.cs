using System.Text;

namespace TgwsOne.Services;

/// <summary>
/// The last lines of a log file, kept up to date: read once on the first <see cref="Refresh"/> (only the end of a big file), then
/// whatever was appended since. The file is only ever read, and not locked against its writer. A file that is replaced
/// (rotation: the old one renamed away, a new one started) is read afresh from its beginning, and one that is missing or off
/// limits is a message (<see cref="Problem"/>), not an exception: logs appear later, and the service's belong to someone else.
/// </summary>
internal sealed class LogTail
{
    /// <summary>How much of the end of a big file is looked at: well over 500 ordinary lines.</summary>
    public const int WindowBytes = 512 * 1024;

    private const int HeadBytes = 64;

    private readonly int _maxLines;
    private readonly List<string> _lines = [];
    private Decoder _decoder = Encoding.UTF8.GetDecoder();
    private readonly StringBuilder _partial = new();   // the end of the file after its last newline: not a line yet
    private long _position;       // how far into the file everything has been read
    private byte[] _head = [];    // the first bytes of the file as last seen: a different start means a different file
    private bool _opened;

    public LogTail(string path, int maxLines = 500)
    {
        Path = path;
        _maxLines = maxLines;
    }

    public string Path { get; }

    /// <summary>The newest lines, oldest first, at most the number asked for.</summary>
    public IReadOnlyList<string> Lines => _lines;

    /// <summary>Why there is nothing to show, or null.</summary>
    public string? Problem { get; private set; }

    /// <summary>Looks at the file again.</summary>
    /// <returns>True when what <see cref="Lines"/> and <see cref="Problem"/> say has changed.</returns>
    public bool Refresh()
    {
        string? problemBefore = Problem;
        int countBefore = _lines.Count;
        string? lastBefore = _lines.Count > 0 ? _lines[^1] : null;
        string? firstBefore = _lines.Count > 0 ? _lines[0] : null;

        try
        {
            using var file = new FileStream(Path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            ReadFrom(file);
            Problem = null;
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
        {
            Forget();
            Problem = "Журнала пока нет: он появится, когда будет что записать.";
        }
        catch (UnauthorizedAccessException)
        {
            Forget();
            Problem = "Нет доступа к этому журналу.";
        }
        catch (IOException ex)
        {
            Problem = $"Не удалось прочитать журнал: {ex.Message}";
        }

        return Problem != problemBefore
            || _lines.Count != countBefore
            || (_lines.Count > 0 && (_lines[^1] != lastBefore || _lines[0] != firstBefore));
    }

    private void ReadFrom(FileStream file)
    {
        long length = file.Length;
        if (!_opened || length < _position || StartChanged(file, length))
        {
            Restart(file, length);
            return;
        }
        if (length > _position)
        {
            file.Seek(_position, SeekOrigin.Begin);
            Append(file, length - _position, dropFirstLine: false);
            _position = length;
            _head = ReadHead(file, length);
        }
    }

    // Is this still the file that was read? Rotation can leave a new file already longer than the old read position by the time
    // of the next look, so the length alone does not tell; the first bytes do.
    private bool StartChanged(FileStream file, long length)
    {
        if (_head.Length == 0) return length > 0 && _position > 0;
        byte[] now = ReadHead(file, Math.Min(length, _head.Length));
        return !now.AsSpan().SequenceEqual(_head);
    }

    private static byte[] ReadHead(FileStream file, long length)
    {
        var head = new byte[Math.Min(length, HeadBytes)];
        file.Seek(0, SeekOrigin.Begin);
        file.ReadExactly(head);
        return head;
    }

    private void Restart(FileStream file, long length)
    {
        Forget();
        _opened = true;
        long start = Math.Max(0, length - WindowBytes);
        file.Seek(start, SeekOrigin.Begin);
        Append(file, length - start, dropFirstLine: start > 0);   // arriving in the middle of a line: that line is not shown
        _position = length;
        _head = ReadHead(file, length);
    }

    private void Forget()
    {
        _lines.Clear();
        _partial.Clear();
        _decoder = Encoding.UTF8.GetDecoder();
        _position = 0;
        _head = [];
        _opened = false;
    }

    private void Append(FileStream file, long count, bool dropFirstLine)
    {
        var buffer = new byte[Math.Min(count, 64 * 1024)];
        var chars = new char[Encoding.UTF8.GetMaxCharCount(buffer.Length)];
        long left = count;
        bool skipping = dropFirstLine;
        while (left > 0)
        {
            int read = file.Read(buffer, 0, (int)Math.Min(buffer.Length, left));
            if (read == 0) break;   // it shrank under us; the next look sorts it out
            left -= read;

            int n = _decoder.GetChars(buffer, 0, read, chars, 0);
            foreach (char c in chars.AsSpan(0, n))
            {
                if (c == '\n')
                {
                    if (!skipping) AddLine(_partial.ToString().TrimEnd('\r'));
                    skipping = false;
                    _partial.Clear();
                }
                else if (!skipping)
                {
                    _partial.Append(c);
                }
            }
        }
    }

    private void AddLine(string line)
    {
        _lines.Add(line);
        if (_lines.Count > _maxLines) _lines.RemoveRange(0, _lines.Count - _maxLines);
    }
}
