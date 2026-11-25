using System.Security.Cryptography;

namespace TgwsOne.Engine.Crypto;

/// <summary>
/// AES in counter mode, the one primitive MTProto obfuscation and the Fake TLS handshake share. .NET has no CTR
/// mode; the counter block is encrypted with AES-ECB (hardware AES-NI on x64/ARM64) and XORed with the data. The
/// counter is a 128-bit big-endian integer, as the <c>cryptography</c> library's CTR mode the engine used.
/// </summary>
public sealed class AesCtr : IDisposable
{
    private const int BlockSize = 16;

    // One AES call per 16-byte block made the relay spend most of its time getting in and out of the crypto library
    // (into OpenSSL on Linux) rather than in AES itself. The keystream is now made in batches of blocks, one call
    // each; never more than this many, and only as many as the read at hand still needs, so that the small MTProto
    // packets that make up most of the traffic do not pay for a whole batch.
    private const int MaxBatchBlocks = 256;

    private readonly Aes _aes;
    private readonly ICryptoTransform _ecb;
    private readonly byte[] _counter = new byte[BlockSize];
    private readonly byte[] _blocks = new byte[MaxBatchBlocks * BlockSize];
    private readonly byte[] _keystream = new byte[MaxBatchBlocks * BlockSize];
    private int _keystreamLength;   // how much of the keystream buffer holds the current batch
    private int _used;              // and how much of that batch was handed out already

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
            if (_used == _keystreamLength)
                Refill(Math.Min(MaxBatchBlocks, (input.Length - i + BlockSize - 1) / BlockSize));
            int count = Math.Min(_keystreamLength - _used, input.Length - i);
            input.Slice(i, count).CopyTo(output.Slice(i, count));
            for (int j = 0; j < count; j++) output[i + j] ^= _keystream[_used + j];
            _used += count;
            i += count;
        }
    }

    /// <summary>Advances the keystream, exactly as if <paramref name="bytes"/> zero bytes had been transformed.</summary>
    public void Skip(int bytes)
    {
        if (bytes <= 0) return;

        // What the buffer still holds is keystream for counter values the counter has already gone past, so dropping
        // it takes no arithmetic. The whole blocks after that are counter increments; a partial block needs keystream.
        int available = _keystreamLength - _used;
        if (bytes <= available)
        {
            _used += bytes;
            return;
        }
        bytes -= available;
        _used = _keystreamLength = 0;

        for (int i = bytes / BlockSize; i > 0; i--) IncrementCounter();
        int rest = bytes % BlockSize;
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

    /// <summary>Encrypts the next <paramref name="blocks"/> counter values into the keystream buffer, a fresh batch.</summary>
    private void Refill(int blocks)
    {
        int length = blocks * BlockSize;
        for (int offset = 0; offset < length; offset += BlockSize)
        {
            _counter.CopyTo(_blocks, offset);
            IncrementCounter();
        }
        _ecb.TransformBlock(_blocks, 0, length, _keystream, 0);
        _keystreamLength = length;
        _used = 0;
    }

    private void IncrementCounter()
    {
        for (int i = BlockSize - 1; i >= 0; i--)
        {
            if (++_counter[i] != 0) break;
        }
    }
}
