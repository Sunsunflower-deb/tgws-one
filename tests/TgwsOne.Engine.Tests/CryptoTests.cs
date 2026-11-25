using System.Security.Cryptography;
using TgwsOne.Engine.Crypto;

namespace TgwsOne.Engine.Tests;

public class AesCtrTests
{
    private static readonly byte[] Key = Convert.FromHexString("2b7e151628aed2a6abf7158809cf4f3c");
    private static readonly byte[] Iv = Convert.FromHexString("f0f1f2f3f4f5f6f7f8f9fafbfcfdfeff");
    private static readonly byte[] Plain = Convert.FromHexString(
        "6bc1bee22e409f96e93d7e117393172a" +
        "ae2d8a571e03ac9c9eb76fac45af8e51" +
        "30c81c46a35ce411e5fbc1191a0a52ef");
    private static readonly byte[] Cipher = Convert.FromHexString(
        "874d6191b620e3261bef6864990db6ce" +
        "9806f66b7970fdff8617187bb9fffdff" +
        "5ae4df3edbd5d35e5b4f09020db03eab");

    [Fact]
    public void MatchesNistCtrVector()
    {
        using var ctr = new AesCtr(Key, Iv);
        Assert.Equal(Cipher, ctr.Transform(Plain));
    }

    [Fact]
    public void IsSymmetric()
    {
        using var encrypt = new AesCtr(Key, Iv);
        byte[] cipher = encrypt.Transform(Plain);
        using var decrypt = new AesCtr(Key, Iv);
        Assert.Equal(Plain, decrypt.Transform(cipher));
    }

    [Fact]
    public void Skip_AdvancesLikeTransformingZeros()
    {
        using var skipped = new AesCtr(Key, Iv);
        skipped.Skip(16);
        byte[] tail = skipped.Transform(Plain.AsSpan(16));

        using var whole = new AesCtr(Key, Iv);
        byte[] full = whole.Transform(Plain);

        Assert.Equal(full.AsSpan(16).ToArray(), tail);
    }

    [Fact]
    public void SkipInsideABlock_Works()
    {
        using var skipped = new AesCtr(Key, Iv);
        skipped.Skip(5);
        byte[] rest = skipped.Transform(Plain.AsSpan(5));

        using var whole = new AesCtr(Key, Iv);
        Assert.Equal(whole.Transform(Plain).AsSpan(5).ToArray(), rest);
    }

    [Fact]
    public void TransformInPlace_MatchesOutputBuffer()
    {
        byte[] a = (byte[])Plain.Clone();
        byte[] b = (byte[])Plain.Clone();
        using (var ctr = new AesCtr(Key, Iv)) ctr.TransformInPlace(a);
        using (var ctr = new AesCtr(Key, Iv)) ctr.Transform(b, b);
        Assert.Equal(Cipher, a);
        Assert.Equal(b, a);
    }

    [Fact]
    public void HandlesUnalignedChunks_AndContinuesTheStream()
    {
        using var one = new AesCtr(Key, Iv);
        using var two = new AesCtr(Key, Iv);
        byte[] chunked = [.. one.Transform(Plain[..7]), .. one.Transform(Plain[7..20]), .. one.Transform(Plain[20..])];
        Assert.Equal(two.Transform(Plain), chunked);
    }
}

public class Obfuscated2Tests
{
    private static readonly byte[] Secret = Convert.FromHexString("0123456789abcdef0123456789abcdef");

    [Theory]
    [InlineData(MtProtoConstants.ProtoIntermediate, 2, false)]
    [InlineData(MtProtoConstants.ProtoAbridged, 4, false)]
    [InlineData(MtProtoConstants.ProtoSecure, 4, true)]
    public void ParsesWhatTheClientSent(uint tag, int dc, bool media)
    {
        var client = new ClientHandshake(Secret, tag, (short)(media ? -dc : dc));

        bool ok = Obfuscated2.TryParseClientHandshake(client.Bytes, Secret,
            out int parsedDc, out bool parsedMedia, out uint parsedTag, out byte[] prekeyIv);

        Assert.True(ok);
        Assert.Equal(dc, parsedDc);
        Assert.Equal(media, parsedMedia);
        Assert.Equal(tag, parsedTag);
        Assert.Equal(48, prekeyIv.Length);
        Assert.Equal(client.Bytes.AsSpan(8, 48).ToArray(), prekeyIv);
    }

    [Fact]
    public void WrongSecret_IsRejected()
    {
        var client = new ClientHandshake(Secret, MtProtoConstants.ProtoIntermediate, 2);
        byte[] other = RandomNumberGenerator.GetBytes(16);
        Assert.False(Obfuscated2.TryParseClientHandshake(client.Bytes, other, out _, out _, out _, out _));
    }

    [Fact]
    public void Garbage_IsRejected()
    {
        Assert.False(Obfuscated2.TryParseClientHandshake(new byte[64], Secret, out _, out _, out _, out _));
        Assert.False(Obfuscated2.TryParseClientHandshake(new byte[10], Secret, out _, out _, out _, out _));
    }

    [Fact]
    public void RelayInit_IsReadableByTelegram()
    {
        byte[] init = Obfuscated2.GenerateRelayInit(MtProtoConstants.ProtoIntermediate, -4);

        Assert.Equal(64, init.Length);
        Assert.NotEqual(0xEF, init[0]);
        Assert.False(init.AsSpan(0, 4).SequenceEqual("HEAD"u8));
        Assert.True(Obfuscated2.TryReadRelayInit(init, out uint tag, out short dc));
        Assert.Equal(MtProtoConstants.ProtoIntermediate, tag);
        Assert.Equal(-4, dc);
    }

    [Fact]
    public void Context_ReencryptsBothDirections()
    {
        var client = new ClientHandshake(Secret, MtProtoConstants.ProtoIntermediate, 2);
        Assert.True(Obfuscated2.TryParseClientHandshake(client.Bytes, Secret,
            out _, out _, out uint tag, out byte[] clientPrekeyIv));
        byte[] relayInit = Obfuscated2.GenerateRelayInit(tag, 2);
        var telegram = new TelegramSide(relayInit);

        using var ctx = new CryptoContext(clientPrekeyIv, Secret, relayInit);
        byte[] up = RandomNumberGenerator.GetBytes(1000);
        byte[] wireUp = client.Encrypt.Transform(up);
        byte[] plainUp = ctx.CltDec.Transform(wireUp);
        Assert.Equal(up, plainUp);
        Assert.Equal(up, telegram.DecryptFromProxy.Transform(ctx.TgEnc.Transform(plainUp)));

        byte[] down = RandomNumberGenerator.GetBytes(1000);
        byte[] wireDown = telegram.EncryptToProxy.Transform(down);
        byte[] plainDown = ctx.TgDec.Transform(wireDown);
        Assert.Equal(down, plainDown);
        Assert.Equal(down, client.Decrypt.Transform(ctx.CltEnc.Transform(plainDown)));
    }
}

/// <summary>
/// The stream as the scheme defines it, with no code shared with <see cref="AesCtr"/>: AES of each counter value in
/// turn. Whatever the engine's implementation does inside, its bytes have to agree with this one.
/// </summary>
public class AesCtrStreamTests
{
    private const int StreamLength = 60_000;

    [Fact]
    public void AnyMixOfReadsAndSkips_FollowsTheCounterStream()
    {
        // Read sizes and skips nobody chose in advance: odd offsets, a skip that ends inside a block and one that
        // crosses many, positions far past any grouping the implementation might keep.
        var rng = new Random(0x5EED);
        for (int round = 0; round < 12; round++)
        {
            byte[] key = Bytes(rng, rng.Next(3) switch { 0 => 16, 1 => 24, _ => 32 });
            byte[] iv = Bytes(rng, 16);
            byte[] plain = Bytes(rng, StreamLength);
            byte[] reference = CounterStream(key, iv, StreamLength);
            using var ctr = new AesCtr(key, iv);

            int position = 0;
            for (int step = 0; step < 60 && position < StreamLength - 4_000; step++)
            {
                if (rng.Next(3) == 0)
                {
                    int skip = rng.Next(0, 4_000);
                    ctr.Skip(skip);
                    position += skip;
                }
                else
                {
                    int count = rng.Next(0, Math.Min(2_500, StreamLength - position));
                    var wire = new byte[count];
                    ctr.Transform(plain.AsSpan(position, count), wire);
                    Assert.Equal(XorWith(reference, plain, position, count), wire);
                    position += count;
                }
            }
        }
    }

    [Fact]
    public void ReadsAndSkipsThatLandOnBlockEdges_FollowTheCounterStream()
    {
        byte[] key = Convert.FromHexString("2b7e151628aed2a6abf7158809cf4f3c");
        byte[] iv = Convert.FromHexString("f0f1f2f3f4f5f6f7f8f9fafbfcfdfeff");
        byte[] plain = Bytes(new Random(1), 30_000);
        byte[] reference = CounterStream(key, iv, plain.Length);
        using var ctr = new AesCtr(key, iv);

        // Each step skips, then reads: the edges of 16-byte blocks and of a 256-block group, and one byte off each.
        (int Skip, int Read)[] steps =
        [
            (0, 16), (0, 1), (15, 16), (0, 4095), (1, 1), (4094, 3), (0, 4096), (0, 17),
            (4095, 2), (4080, 16), (8192, 1), (31, 33), (0, 0), (48, 12),
        ];
        int position = 0;
        foreach ((int skip, int read) in steps)
        {
            ctr.Skip(skip);
            position += skip;
            var wire = new byte[read];
            ctr.Transform(plain.AsSpan(position, read), wire);
            Assert.Equal(XorWith(reference, plain, position, read), wire);
            position += read;
        }
    }

    /// <summary>The keystream from the first block on: AES of IV, IV + 1, IV + 2, … as 128-bit big-endian numbers.</summary>
    private static byte[] CounterStream(byte[] key, byte[] iv, int length)
    {
        using var aes = Aes.Create();
        aes.Key = key;
        aes.Mode = CipherMode.ECB;
        aes.Padding = PaddingMode.None;
        var counter = new byte[16];
        iv.CopyTo(counter, 0);
        var block = new byte[16];
        var stream = new byte[length];
        for (int offset = 0; offset < length; offset += 16)
        {
            aes.EncryptEcb(counter, block, PaddingMode.None);
            block.AsSpan(0, Math.Min(16, length - offset)).CopyTo(stream.AsSpan(offset));
            for (int i = 15; i >= 0; i--) if (++counter[i] != 0) break;
        }
        return stream;
    }

    private static byte[] Bytes(Random rng, int count)
    {
        var bytes = new byte[count];
        rng.NextBytes(bytes);
        return bytes;
    }

    /// <summary>What a correct stream gives for a read of <paramref name="plain"/> at <paramref name="position"/>.</summary>
    private static byte[] XorWith(byte[] stream, byte[] plain, int position, int count)
    {
        var wire = new byte[count];
        for (int i = 0; i < count; i++) wire[i] = (byte)(plain[position + i] ^ stream[position + i]);
        return wire;
    }
}
