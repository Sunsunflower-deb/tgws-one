using System.Buffers.Binary;
using System.Runtime.InteropServices;
using TgwsOne.Engine.Crypto;

namespace TgwsOne.Engine.MtProto;

/// <summary>
/// Splits the byte stream from Telegram Desktop into individual MTProto transport packets so each packet becomes
/// its own WebSocket frame. Telegram Desktop may coalesce several packets into one TLS/TCP write; Telegram's
/// WebSocket endpoint expects frame-per-packet. The ciphertext is queued alongside the plaintext because packet
/// lengths are only readable after decryption, and the parts sent on must be the original ciphertext.
/// </summary>
public sealed class MsgSplitter
{
    private readonly uint _proto;
    private readonly List<byte> _cipher = [];
    private readonly List<byte> _plain = [];
    private bool _disabled;

    public MsgSplitter(uint protoTag) => _proto = protoTag;

    public IReadOnlyList<byte[]> Split(ReadOnlySpan<byte> cipherChunk, ReadOnlySpan<byte> plainChunk)
    {
        if (cipherChunk.IsEmpty) return [];
        if (_disabled) return [cipherChunk.ToArray()];

        _cipher.AddRange(cipherChunk);
        _plain.AddRange(plainChunk);

        var parts = new List<byte[]>();
        int offset = 0;
        int bufferLength = _cipher.Count;
        while (offset < bufferLength)
        {
            int? packetLength = NextPacketLength(offset, bufferLength - offset);
            if (packetLength is null) break;
            if (packetLength <= 0)
            {
                parts.Add(offset == 0 ? [.. _cipher] : [.. _cipher.GetRange(offset, bufferLength - offset)]);
                offset = bufferLength;
                _disabled = true;
                break;
            }
            if (offset == 0 && packetLength == bufferLength)
                parts.Add([.. _cipher]);
            else
                parts.Add([.. _cipher.GetRange(offset, packetLength.Value)]);
            offset += packetLength.Value;
        }

        if (offset > 0)
        {
            _cipher.RemoveRange(0, offset);
            _plain.RemoveRange(0, offset);
        }
        return parts;
    }

    /// <summary>Whatever is left when the client is done writing: sent as one last frame.</summary>
    public IReadOnlyList<byte[]> Flush()
    {
        if (_cipher.Count == 0) return [];
        byte[] tail = [.. _cipher];
        _cipher.Clear();
        _plain.Clear();
        return [tail];
    }

    private int? NextPacketLength(int offset, int available)
    {
        if (available <= 0) return null;
        if (_proto == MtProtoConstants.ProtoAbridged) return NextAbridgedLength(offset, available);
        if (_proto is MtProtoConstants.ProtoIntermediate or MtProtoConstants.ProtoSecure) return NextIntermediateLength(offset, available);
        return 0;
    }

    private int? NextAbridgedLength(int offset, int available)
    {
        int first = _plain[offset];
        int payloadLength, headerLength;
        if (first is 0x7F or 0xFF)
        {
            if (available < 4) return null;
            payloadLength = (_plain[offset + 1] | (_plain[offset + 2] << 8) | (_plain[offset + 3] << 16)) * 4;
            headerLength = 4;
        }
        else
        {
            payloadLength = (first & 0x7F) * 4;
            headerLength = 1;
        }
        if (payloadLength <= 0) return 0;
        int packetLength = headerLength + payloadLength;
        return available < packetLength ? null : packetLength;
    }

    private int? NextIntermediateLength(int offset, int available)
    {
        if (available < 4) return null;
        Span<byte> plain = CollectionsMarshal.AsSpan(_plain);
        uint value = BinaryPrimitives.ReadUInt32LittleEndian(plain.Slice(offset, 4));
        int payloadLength = (int)(value & 0x7FFFFFFF);
        if (payloadLength <= 0) return 0;
        int packetLength = 4 + payloadLength;
        return available < packetLength ? null : packetLength;
    }
}
