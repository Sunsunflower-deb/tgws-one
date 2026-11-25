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
