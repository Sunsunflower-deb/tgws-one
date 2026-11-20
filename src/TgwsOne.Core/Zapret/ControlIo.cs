using System.Text;

namespace TgwsOne.Core.Zapret;

/// <summary>What <see cref="ControlIo.ReadLineAsync"/> found: a line (null if the peer closed without sending anything), or too much data.</summary>
internal readonly record struct LineRead(string? Line, bool TooLong, bool SawNewline);

internal static class ControlIo
{
    /// <summary>
    /// Reads up to the first newline (a trailing CR is dropped; the end of the stream also ends a line).
    /// A line longer than <paramref name="maxBytes"/> is reported without reading the rest of it.
    /// </summary>
    public static async Task<LineRead> ReadLineAsync(Stream stream, int maxBytes, CancellationToken ct)
    {
        var buffer = new byte[4096];
        using var acc = new MemoryStream();
        bool sawNewline = false;
        while (!sawNewline)
        {
            int n = await stream.ReadAsync(buffer, ct);
            if (n == 0) break;
            int nl = Array.IndexOf(buffer, (byte)'\n', 0, n);
            sawNewline = nl >= 0;
            int take = sawNewline ? nl : n;
            if (acc.Length + take > maxBytes + 1) return new LineRead(null, TooLong: true, sawNewline);   // +1: a CR may follow the limit
            acc.Write(buffer, 0, take);
        }

        if (acc.Length == 0 && !sawNewline) return new LineRead(null, TooLong: false, SawNewline: false);
        byte[] bytes = acc.ToArray();
        int len = bytes.Length > 0 && bytes[^1] == '\r' ? bytes.Length - 1 : bytes.Length;
        if (len > maxBytes) return new LineRead(null, TooLong: true, sawNewline);
        return new LineRead(Encoding.UTF8.GetString(bytes, 0, len), TooLong: false, sawNewline);
    }

    /// <summary>
    /// Discards incoming data up to the next newline (or the end of the stream, or <paramref name="maxBytes"/>).
    /// Closing a socket with unread data resets the connection, and the peer would lose the answer already sent.
    /// </summary>
    public static async Task DrainLineAsync(Stream stream, long maxBytes, CancellationToken ct)
    {
        var buffer = new byte[16 * 1024];
        long total = 0;
        while (total < maxBytes)
        {
            int n = await stream.ReadAsync(buffer, ct);
            if (n == 0 || Array.IndexOf(buffer, (byte)'\n', 0, n) >= 0) return;
            total += n;
        }
    }

    public static async Task WriteLineAsync(Stream stream, string json, CancellationToken ct)
    {
        await stream.WriteAsync(Encoding.UTF8.GetBytes(json + "\n"), ct);
        await stream.FlushAsync(ct);
    }
}
