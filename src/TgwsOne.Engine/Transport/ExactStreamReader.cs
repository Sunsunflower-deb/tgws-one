namespace TgwsOne.Engine.Transport;

/// <summary>
/// A small buffered reader for framing protocols. <see cref="Stream.ReadExactlyAsync(Memory{byte}, CancellationToken)"/>
/// on a raw network stream costs one syscall (or one TLS record decrypt) per tiny header read; WebSocket frames are
/// two bytes of header followed by a payload, so buffering reads turns that into one read per burst.
/// </summary>
internal sealed class ExactStreamReader(Stream stream, int bufferSize = 16 * 1024)
{
    private readonly byte[] _buffer = new byte[bufferSize];
    private int _position;
    private int _length;

    public async ValueTask<int> ReadAsync(Memory<byte> destination, CancellationToken ct)
    {
        if (_length - _position == 0)
        {
            if (!await FillAsync(ct).ConfigureAwait(false)) return 0;
        }
        int count = Math.Min(destination.Length, _length - _position);
        _buffer.AsMemory(_position, count).CopyTo(destination);
        _position += count;
        return count;
    }

    public async ValueTask ReadExactlyAsync(Memory<byte> destination, CancellationToken ct)
    {
        int done = 0;
        while (done < destination.Length)
        {
            int read = await ReadAsync(destination[done..], ct).ConfigureAwait(false);
            if (read == 0) throw new EndOfStreamException($"unexpected end of stream after {done} of {destination.Length} bytes");
            done += read;
        }
    }

    public async ValueTask<byte[]> ReadExactlyAsync(int count, CancellationToken ct)
    {
        var result = new byte[count];
        await ReadExactlyAsync(result, ct).ConfigureAwait(false);
        return result;
    }

    /// <summary>A CRLF- or LF-terminated line without the terminator; null at end of stream.</summary>
    public async ValueTask<string?> ReadLineAsync(int maxLength, CancellationToken ct)
    {
        var line = new List<byte>(Math.Min(maxLength, 128));
        while (line.Count <= maxLength)
        {
            if (_length - _position == 0 && !await FillAsync(ct).ConfigureAwait(false))
                return line.Count == 0 ? null : System.Text.Encoding.UTF8.GetString([.. line]);
            byte b = _buffer[_position++];
            if (b == (byte)'\n')
            {
                if (line.Count > 0 && line[^1] == (byte)'\r') line.RemoveAt(line.Count - 1);
                return System.Text.Encoding.UTF8.GetString([.. line]);
            }
            line.Add(b);
        }
        throw new InvalidDataException("HTTP header line too long");
    }

    private async ValueTask<bool> FillAsync(CancellationToken ct)
    {
        _position = 0;
        _length = await stream.ReadAsync(_buffer, ct).ConfigureAwait(false);
        return _length > 0;
    }
}
