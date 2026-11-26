using System.Buffers.Binary;
using System.Security.Cryptography;
using TgwsOne.Engine.Crypto;

namespace TgwsOne.Engine.MtProto;

/// <summary>
/// Reads and writes whole MTProto transport packets on the client side of the HTTP/2 media route, where each
/// request carries exactly one packet: the WebSocket route can rely on frame boundaries, this cannot.
/// </summary>
public static class PacketCodec
{
    public const int MaxPacket = 4 * 1024 * 1024;
    private const int MaxPlainPadding = 256;

    /// <param name="tag">The protocol the client's handshake announced (abridged, intermediate or padded intermediate).</param>
    /// <returns>The packet body, plus the abridged "quick ack" bit: it is carried but not acted on, as upstream.</returns>
    public static async Task<(byte[] Body, bool QuickAck)> ReadPacketAsync(
        Stream stream, AesCtr decryptor, uint tag, CancellationToken ct)
    {
        async Task<byte[]> Plain(int count)
        {
            var encrypted = new byte[count];
            await stream.ReadExactlyAsync(encrypted, ct).ConfigureAwait(false);
            return decryptor.Transform(encrypted);
        }

        int length;
        bool quickAck;
        if (tag == MtProtoConstants.ProtoAbridged)
        {
            byte first = (await Plain(1).ConfigureAwait(false))[0];
            quickAck = (first & 0x80) != 0;
            int words = first & 0x7F;
            if (words == 0x7F)
            {
                byte[] extended = await Plain(3).ConfigureAwait(false);
                words = extended[0] | (extended[1] << 8) | (extended[2] << 16);
            }
            length = words * 4;
        }
        else
        {
            byte[] value = await Plain(4).ConfigureAwait(false);
            uint packed = BinaryPrimitives.ReadUInt32LittleEndian(value);
            quickAck = (packed & 0x80000000) != 0;
            length = (int)(packed & 0x7FFFFFFF);
        }

        int maxLength = MaxPacket + (tag == MtProtoConstants.ProtoSecure ? MaxPlainPadding : 0);
        if (length < 24 || length > maxLength)
            throw new InvalidDataException($"native packet length outside supported range: {length}");

        byte[] body = await Plain(length).ConfigureAwait(false);
        if (tag == MtProtoConstants.ProtoSecure)
        {
            int packetLength, maxPadding;
            if (body.AsSpan(0, 8).SequenceEqual(new byte[8]))
            {
                packetLength = 20 + BinaryPrimitives.ReadInt32LittleEndian(body.AsSpan(16, 4));
                maxPadding = MaxPlainPadding;
            }
            else
            {
                packetLength = 24 + ((length - 24) / 16) * 16;
                maxPadding = 15;
            }
            int padding = length - packetLength;
            if (packetLength < 24 || packetLength > length || padding > maxPadding)
                throw new InvalidDataException(
                    $"invalid padded native packet: frame={length} packet={packetLength} padding={padding} limit={maxPadding}");
            body = body[..packetLength];
        }

        if (body.Length % 4 != 0 || body.Length > MaxPacket)
            throw new InvalidDataException("invalid native packet alignment or size");
        return (body, quickAck);
    }

    public static byte[] EncodeReply(ReadOnlySpan<byte> body, uint tag)
    {
        if (body.IsEmpty || body.Length % 4 != 0 || body.Length > MaxPacket)
            throw new InvalidDataException($"invalid HTTP MTProto response length: {body.Length}");

        if (tag == MtProtoConstants.ProtoAbridged)
        {
            int words = body.Length / 4;
            byte[] prefix = words < 0x7F
                ? [(byte)words]
                : [0x7F, (byte)(words & 0xFF), (byte)((words >> 8) & 0xFF), (byte)((words >> 16) & 0xFF)];
            var result = new byte[prefix.Length + body.Length];
            prefix.CopyTo(result, 0);
            body.CopyTo(result.AsSpan(prefix.Length));
            return result;
        }

        byte[] padding = tag == MtProtoConstants.ProtoSecure
            ? RandomNumberGenerator.GetBytes(RandomNumberGenerator.GetInt32(0, 4))
            : [];
        var output = new byte[4 + body.Length + padding.Length];
        BinaryPrimitives.WriteInt32LittleEndian(output, body.Length + padding.Length);
        body.CopyTo(output.AsSpan(4));
        padding.CopyTo(output, 4 + body.Length);
        return output;
    }
}
