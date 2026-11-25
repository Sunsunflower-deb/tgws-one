using System.Security.Cryptography;

namespace TgwsOne.Engine.Crypto;

/// <summary>
/// AES in counter mode, the one primitive MTProto obfuscation and the Fake TLS handshake share. .NET has no CTR
/// mode; the counter block is encrypted with AES-ECB (hardware AES-NI on x64/ARM64) and XORed with the data. The
/// counter is a 128-bit big-endian integer, as the <c>cryptography</c> library's CTR mode the engine used.
/// </summary>
public sealed class AesCtr : IDisposable
{
    private readonly Aes _aes;
    private readonly ICryptoTransform _ecb;
    private readonly byte[] _counter = new byte[16];
    private readonly byte[] _keystream = new byte[16];
    private int _used = 16;

    public AesCtr(ReadOnlySpan<byte> key, ReadOnlySpan<byte> iv)
    {
        if (key.Length is not (16 or 24 or 32)) throw new ArgumentException("AES key must be 16/24/32 bytes", nameof(key));
        if (iv.Length != 16) throw new ArgumentException("CTR IV must be 16 bytes", nameof(iv));
        iv.CopyTo(_counter);

        _aes = Aes.Create();
        _aes.Key = key.ToArray();
        _aes.Mode = CipherMode.ECB;
        _aes.Padding = PaddingMode.None;
        _ecb = _aes.CreateEncryptor();
    }

    public byte[] Transform(ReadOnlySpan<byte> input)
    {
        var output = new byte[input.Length];
        Transform(input, output);
        return output;
    }

    /// <summary>In-place variant: <paramref name="data"/> is transformed where it lies.</summary>
    public void TransformInPlace(Span<byte> data) => Transform(data, data);

    public void Transform(ReadOnlySpan<byte> input, Span<byte> output)
    {
        if (output.Length < input.Length) throw new ArgumentException("output is shorter than input", nameof(output));
        int i = 0;
        while (i < input.Length)
        {
            if (_used == 16)
            {
                _ecb.TransformBlock(_counter, 0, 16, _keystream, 0);
                IncrementCounter();
                _used = 0;
            }
            int count = Math.Min(16 - _used, input.Length - i);
            for (int j = 0; j < count; j++) output[i + j] = (byte)(input[i + j] ^ _keystream[_used + j]);
            _used += count;
            i += count;
        }
    }

    /// <summary>Advances the keystream, exactly as if <paramref name="bytes"/> zero bytes had been transformed.</summary>
    public void Skip(int bytes)
    {
        if (bytes <= 0) return;

        // Consume what is left of the current keystream block first, if the stream is mid-block.
        if (_used != 16)
        {
            int available = 16 - _used;
            if (bytes <= available)
            {
                _used += bytes;
                return;
            }
            bytes -= available;
            _used = 16;
        }

        int whole = bytes / 16;
        int rest = bytes % 16;
        // Whole blocks are just counter arithmetic; only a partial block needs a keystream block.
        for (int i = 0; i < whole; i++) IncrementCounter();
        if (rest == 0) return;
        Span<byte> zeros = stackalloc byte[rest];
        Span<byte> scratch = stackalloc byte[rest];
        zeros.Clear();
        Transform(zeros, scratch);
    }

    public void Dispose()
    {
        _ecb.Dispose();
        _aes.Dispose();
    }

    private void IncrementCounter()
    {
        for (int i = 15; i >= 0; i--)
        {
            if (++_counter[i] != 0) break;
        }
    }
}
