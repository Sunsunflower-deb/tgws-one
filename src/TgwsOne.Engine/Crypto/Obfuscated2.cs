using System.Buffers.Binary;
using System.Security.Cryptography;

namespace TgwsOne.Engine.Crypto;

/// <summary>The wire constants of MTProto's obfuscated2 transport (upstream <c>proxy/utils.py</c>).</summary>
public static class MtProtoConstants
{
    public const int HandshakeLength = 64;
    public const int SkipLength = 8;
    public const int PrekeyLength = 32;
    public const int KeyLength = 32;
    public const int IvLength = 16;
    public const int ProtoTagPosition = 56;
    public const int DcIndexPosition = 60;

    public const uint ProtoAbridged = 0xEFEF_EFEF;
    public const uint ProtoIntermediate = 0xEEEE_EEEE;
    public const uint ProtoSecure = 0xDDDD_DDDD;

    public static readonly byte[] TagAbridged = [0xEF, 0xEF, 0xEF, 0xEF];
    public static readonly byte[] TagIntermediate = [0xEE, 0xEE, 0xEE, 0xEE];
    public static readonly byte[] TagSecure = [0xDD, 0xDD, 0xDD, 0xDD];

    private const byte ReservedFirstByte = 0xEF;

    public static uint ProtoTagToInt(ReadOnlySpan<byte> tag)
    {
        if (tag.SequenceEqual(TagAbridged)) return ProtoAbridged;
        if (tag.SequenceEqual(TagIntermediate)) return ProtoIntermediate;
        return ProtoSecure;
    }

    public static bool IsKnownTag(ReadOnlySpan<byte> tag)
        => tag.SequenceEqual(TagAbridged) || tag.SequenceEqual(TagIntermediate) || tag.SequenceEqual(TagSecure);
}

/// <summary>
/// The obfuscated2 handshake: the client encrypts its first 64 bytes with AES-CTR keyed by SHA256(prekey + secret);
/// the proxy decrypts them, learns the protocol and DC, and answers the relay's own 64-byte random handshake.
/// </summary>
public static class Obfuscated2
{
    /// <param name="clientDecPrekeyIv">The 48 bytes at offset 8 of the client's handshake, for <see cref="CryptoContext"/>.</param>
    public static bool TryParseClientHandshake(ReadOnlySpan<byte> handshake, ReadOnlySpan<byte> secret,
        out int dc, out bool isMedia, out uint protoTag, out byte[] clientDecPrekeyIv)
    {
        dc = 0;
        isMedia = false;
        protoTag = 0;
        clientDecPrekeyIv = [];
        if (handshake.Length < MtProtoConstants.HandshakeLength) return false;

        ReadOnlySpan<byte> prekeyIv = handshake.Slice(MtProtoConstants.SkipLength, MtProtoConstants.PrekeyLength + MtProtoConstants.IvLength);
        Span<byte> key = stackalloc byte[32];
        SHA256.HashData([.. prekeyIv[..MtProtoConstants.PrekeyLength], .. secret], key);

        var decrypted = new byte[MtProtoConstants.HandshakeLength];
        using (var ctr = new AesCtr(key, prekeyIv.Slice(MtProtoConstants.PrekeyLength, MtProtoConstants.IvLength)))
            ctr.Transform(handshake[..MtProtoConstants.HandshakeLength], decrypted);

        ReadOnlySpan<byte> tag = decrypted.AsSpan(MtProtoConstants.ProtoTagPosition, 4);
        if (!MtProtoConstants.IsKnownTag(tag)) return false;

        short dcIdx = BinaryPrimitives.ReadInt16LittleEndian(decrypted.AsSpan(MtProtoConstants.DcIndexPosition, 2));
        dc = Math.Abs((int)dcIdx);
        isMedia = dcIdx < 0;
        protoTag = MtProtoConstants.ProtoTagToInt(tag);
        clientDecPrekeyIv = prekeyIv.ToArray();
        return true;
    }

    /// <summary>
    /// The 64 random-looking bytes the proxy sends to Telegram. Their tail carries the protocol tag and the signed
    /// DC number, encrypted under the key the bytes themselves name; every byte before the tail is random.
    /// </summary>
    public static byte[] GenerateRelayInit(uint protoTag, int dcIdx)
    {
        while (true)
        {
            byte[] random = new byte[MtProtoConstants.HandshakeLength];
            RandomNumberGenerator.Fill(random);
            if (random[0] == 0xEF) continue;
            if (MtProtoConstants.IsKnownTag(random.AsSpan(0, 4))) continue;
            if (random.AsSpan(0, 4).SequenceEqual("HEAD"u8) || random.AsSpan(0, 4).SequenceEqual("POST"u8)
                || random.AsSpan(0, 4).SequenceEqual("GET "u8) || random.AsSpan(0, 4).SequenceEqual("OPTI"u8)
                || random.AsSpan(0, 4).SequenceEqual(new byte[] { 0x16, 0x03, 0x01, 0x02 }))
                continue;
            if (random[4] == 0 && random[5] == 0 && random[6] == 0 && random[7] == 0) continue;
            return SealRelayInit(random, protoTag, (short)dcIdx);
        }
    }

    /// <summary>
    /// The 64 random bytes with the protocol tag and the signed DC number sealed into their tail: Telegram decrypts
    /// the tail with the key the bytes themselves name and reads the tag and the DC from it. The two bytes after the
    /// DC are padding; they are the buffer's own last two bytes, which the sealed tail replaces anyway.
    /// </summary>
    internal static byte[] SealRelayInit(ReadOnlySpan<byte> random, uint protoTag, short dcIdx)
    {
        Span<byte> tailPlain = stackalloc byte[8];
        BinaryPrimitives.WriteUInt32LittleEndian(tailPlain, protoTag);
        BinaryPrimitives.WriteInt16LittleEndian(tailPlain[4..], dcIdx);
        random.Slice(MtProtoConstants.ProtoTagPosition + 6, 2).CopyTo(tailPlain[6..8]);

        using var ctr = new AesCtr(random.Slice(MtProtoConstants.SkipLength, MtProtoConstants.PrekeyLength),
            random.Slice(MtProtoConstants.SkipLength + MtProtoConstants.PrekeyLength, MtProtoConstants.IvLength));
        byte[] encryptedFull = ctr.Transform(random);

        byte[] relayInit = random.ToArray();
        for (int i = 0; i < 8; i++)
            relayInit[MtProtoConstants.ProtoTagPosition + i] =
                (byte)(tailPlain[i] ^ encryptedFull[MtProtoConstants.ProtoTagPosition + i] ^ random[MtProtoConstants.ProtoTagPosition + i]);

        return relayInit;
    }

    /// <summary>Verifies the tail of a relay init the way Telegram will: decrypt it with the key it names.</summary>
    public static bool TryReadRelayInit(ReadOnlySpan<byte> relayInit, out uint protoTag, out short dcIdx)
    {
        protoTag = 0;
        dcIdx = 0;
        if (relayInit.Length < MtProtoConstants.HandshakeLength) return false;
        ReadOnlySpan<byte> key = relayInit.Slice(MtProtoConstants.SkipLength, MtProtoConstants.PrekeyLength);
        ReadOnlySpan<byte> iv = relayInit.Slice(MtProtoConstants.SkipLength + MtProtoConstants.PrekeyLength, MtProtoConstants.IvLength);
        using var ctr = new AesCtr(key, iv);
        Span<byte> decrypted = stackalloc byte[MtProtoConstants.HandshakeLength];
        ctr.Transform(relayInit[..MtProtoConstants.HandshakeLength], decrypted);
        protoTag = BinaryPrimitives.ReadUInt32LittleEndian(decrypted[MtProtoConstants.ProtoTagPosition..]);
        dcIdx = BinaryPrimitives.ReadInt16LittleEndian(decrypted[(MtProtoConstants.ProtoTagPosition + 4)..]);
        return MtProtoConstants.IsKnownTag(decrypted.Slice(MtProtoConstants.ProtoTagPosition, 4));
    }
}

/// <summary>
/// The four AES-CTR streams of one bridged connection: decrypt from the client, encrypt to the client, encrypt to
/// Telegram, decrypt from Telegram. The client's streams are keyed from its handshake and the shared secret; the
/// Telegram side from the relay handshake we generated (no secret hash there, just the raw 32-byte key).
/// </summary>
public sealed class CryptoContext : IDisposable
{
    public AesCtr CltDec { get; }
    public AesCtr CltEnc { get; }
    public AesCtr TgEnc { get; }
    public AesCtr TgDec { get; }

    public CryptoContext(ReadOnlySpan<byte> clientDecPrekeyIv, ReadOnlySpan<byte> secret, ReadOnlySpan<byte> relayInit)
    {
        byte[] cltDecPrekey = clientDecPrekeyIv[..MtProtoConstants.PrekeyLength].ToArray();
        byte[] cltDecIv = clientDecPrekeyIv.Slice(MtProtoConstants.PrekeyLength, MtProtoConstants.IvLength).ToArray();
        byte[] cltDecKey = SHA256.HashData([.. cltDecPrekey, .. secret]);

        byte[] reversed = clientDecPrekeyIv[..(MtProtoConstants.PrekeyLength + MtProtoConstants.IvLength)].ToArray();
        Array.Reverse(reversed);
        byte[] cltEncKey = SHA256.HashData([.. reversed[..MtProtoConstants.PrekeyLength], .. secret]);
        byte[] cltEncIv = reversed[MtProtoConstants.PrekeyLength..(MtProtoConstants.PrekeyLength + MtProtoConstants.IvLength)];

        CltDec = new AesCtr(cltDecKey, cltDecIv);
        CltEnc = new AesCtr(cltEncKey, cltEncIv);
        CltDec.Skip(MtProtoConstants.HandshakeLength);   // the 64-byte handshake was already consumed by the parser

        ReadOnlySpan<byte> relayEncKey = relayInit.Slice(MtProtoConstants.SkipLength, MtProtoConstants.KeyLength);
        ReadOnlySpan<byte> relayEncIv = relayInit.Slice(MtProtoConstants.SkipLength + MtProtoConstants.PrekeyLength, MtProtoConstants.IvLength);

        byte[] relayReversed = relayInit.Slice(MtProtoConstants.SkipLength, MtProtoConstants.PrekeyLength + MtProtoConstants.IvLength).ToArray();
        Array.Reverse(relayReversed);
        byte[] relayDecKey = relayReversed[..MtProtoConstants.KeyLength];
        byte[] relayDecIv = relayReversed[MtProtoConstants.KeyLength..(MtProtoConstants.KeyLength + MtProtoConstants.IvLength)];

        TgEnc = new AesCtr(relayEncKey, relayEncIv);
        TgDec = new AesCtr(relayDecKey, relayDecIv);
        TgEnc.Skip(MtProtoConstants.HandshakeLength);
    }

    public void Dispose()
    {
        CltDec.Dispose();
        CltEnc.Dispose();
        TgEnc.Dispose();
        TgDec.Dispose();
    }
}
