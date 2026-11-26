using System.Buffers.Binary;
using System.Security.Cryptography;

namespace TgwsOne.Engine.Crypto;

/// <summary>
/// A Telegram Desktop client's side of the obfuscated2 handshake, built the way the real client builds it: the
/// 64 random bytes name the key, the tail carries the protocol tag and DC, and the same AES-CTR stream continues
/// into the data. The relay side is the reverse key, as in upstream <c>_build_crypto_ctx</c>. The self-check and the
/// engine tests speak the same handshake, so it lives here rather than in either of them.
/// </summary>
public sealed class ClientHandshake
{
    public byte[] Bytes { get; }
    public AesCtr Encrypt { get; }
    public AesCtr Decrypt { get; }
    public uint ProtoTag { get; }

    public ClientHandshake(byte[] secret, uint protoTag, short dcIdx)
    {
        ProtoTag = protoTag;
        // Like the real client and upstream's relay-init generator: bytes 0..56 go out as the random key material
        // they look like, and only the 8-byte tail is encrypted with a keystream the proxy can reproduce.
        Bytes = new byte[MtProtoConstants.HandshakeLength];
        RandomNumberGenerator.Fill(Bytes);

        byte[] prekey = Bytes.AsSpan(MtProtoConstants.SkipLength, MtProtoConstants.PrekeyLength).ToArray();
        byte[] iv = Bytes.AsSpan(MtProtoConstants.SkipLength + MtProtoConstants.PrekeyLength,
            MtProtoConstants.IvLength).ToArray();
        byte[] key = SHA256.HashData([.. prekey, .. secret]);

        var tail = new byte[8];
        BinaryPrimitives.WriteUInt32LittleEndian(tail.AsSpan(0, 4), protoTag);
        BinaryPrimitives.WriteInt16LittleEndian(tail.AsSpan(4, 2), dcIdx);
        RandomNumberGenerator.Fill(tail.AsSpan(6, 2));

        using (var handshakeCtr = new AesCtr(key, iv))
        {
            byte[] encrypted = handshakeCtr.Transform(Bytes);
            for (int i = 0; i < 8; i++)
            {
                int at = MtProtoConstants.ProtoTagPosition + i;
                Bytes[at] = (byte)(tail[i] ^ encrypted[at] ^ Bytes[at]);
            }
        }

        Encrypt = new AesCtr(key, iv);
        Encrypt.Skip(MtProtoConstants.HandshakeLength);

        byte[] reversed = [.. Bytes.AsSpan(MtProtoConstants.SkipLength,
            MtProtoConstants.PrekeyLength + MtProtoConstants.IvLength)];
        Array.Reverse(reversed);
        byte[] decKey = SHA256.HashData([.. reversed.AsSpan(0, MtProtoConstants.PrekeyLength), .. secret]);
        byte[] decIv = reversed.AsSpan(MtProtoConstants.PrekeyLength, MtProtoConstants.IvLength).ToArray();
        Decrypt = new AesCtr(decKey, decIv);
    }
}
