using System.Buffers.Binary;
using System.Security.Cryptography;
using TgwsOne.Engine.Crypto;
using TgwsOne.Engine.MtProto;

namespace TgwsOne.Engine.Tests;

public class MsgSplitterTests
{
    private static byte[] AbridgedPacket(int payloadBytes)
    {
        Assert.Equal(0, payloadBytes % 4);
        int words = payloadBytes / 4;
        var packet = new byte[1 + payloadBytes];
        packet[0] = (byte)words;
        RandomNumberGenerator.Fill(packet.AsSpan(1));
        return packet;
    }

    private static byte[] ExtendedAbridgedPacket(int payloadBytes)
    {
        int words = payloadBytes / 4;
        var packet = new byte[4 + payloadBytes];
        packet[0] = 0x7F;
        packet[1] = (byte)(words & 0xFF);
        packet[2] = (byte)((words >> 8) & 0xFF);
        packet[3] = (byte)((words >> 16) & 0xFF);
        RandomNumberGenerator.Fill(packet.AsSpan(4));
        return packet;
    }

    private static byte[] IntermediatePacket(int payloadBytes)
    {
        var packet = new byte[4 + payloadBytes];
        BinaryPrimitives.WriteInt32LittleEndian(packet, payloadBytes);
        RandomNumberGenerator.Fill(packet.AsSpan(4));
        return packet;
    }

    [Fact]
    public void Abridged_OnePacket_IsOnePart()
    {
        byte[] packet = AbridgedPacket(8);
        var parts = new MsgSplitter(MtProtoConstants.ProtoAbridged).Split(packet, packet);
        Assert.Single(parts);
        Assert.Equal(packet, parts[0]);
    }

    [Fact]
    public void Abridged_TwoPacketsInOneChunk_AreTwoParts()
    {
        byte[] a = AbridgedPacket(8);
        byte[] b = AbridgedPacket(12);
        byte[] chunk = [.. a, .. b];

        var parts = new MsgSplitter(MtProtoConstants.ProtoAbridged).Split(chunk, chunk);

        Assert.Equal(2, parts.Count);
        Assert.Equal(a, parts[0]);
        Assert.Equal(b, parts[1]);
    }

    [Fact]
    public void Abridged_ExtendedLength_IsUnderstood()
    {
        byte[] packet = ExtendedAbridgedPacket(127 * 4);
        var parts = new MsgSplitter(MtProtoConstants.ProtoAbridged).Split(packet, packet);
        Assert.Single(parts);
        Assert.Equal(packet, parts[0]);
    }

    [Fact]
    public void Abridged_AcrossChunks_IsBufferedUntilComplete()
    {
        byte[] packet = AbridgedPacket(16);
        var splitter = new MsgSplitter(MtProtoConstants.ProtoAbridged);

        Assert.Empty(splitter.Split(packet.AsSpan(0, 5), packet.AsSpan(0, 5)));
        var parts = splitter.Split(packet.AsSpan(5), packet.AsSpan(5));

        Assert.Single(parts);
        Assert.Equal(packet, parts[0]);
    }

    [Fact]
    public void Intermediate_AndPadded_UseTheLengthPrefix()
    {
        foreach (uint tag in new[] { MtProtoConstants.ProtoIntermediate, MtProtoConstants.ProtoSecure })
        {
            byte[] a = IntermediatePacket(8);
            byte[] b = IntermediatePacket(16);
            var parts = new MsgSplitter(tag).Split([.. a, .. b], [.. a, .. b]);
            Assert.Equal(2, parts.Count);
            Assert.Equal(a, parts[0]);
            Assert.Equal(b, parts[1]);
        }
    }

    [Fact]
    public void ZeroLength_DisablesSplitting_AndPassesTheRestThrough()
    {
        byte[] bad = new byte[4];   // a zero-length intermediate packet
        byte[] chunk = [.. bad, 1, 2, 3, 4];
        var splitter = new MsgSplitter(MtProtoConstants.ProtoIntermediate);

        var parts = splitter.Split(chunk, chunk);

        Assert.Single(parts);
        Assert.Equal(chunk, parts[0]);
        Assert.Equal(chunk, Assert.Single(splitter.Split(chunk, chunk)));
    }

    [Fact]
    public void Flush_ReturnsTheIncompleteTail()
    {
        byte[] packet = AbridgedPacket(16);
        var splitter = new MsgSplitter(MtProtoConstants.ProtoAbridged);
        Assert.Empty(splitter.Split(packet.AsSpan(0, 6), packet.AsSpan(0, 6)));

        var tail = splitter.Flush();

        Assert.Single(tail);
        Assert.Equal(packet.AsSpan(0, 6).ToArray(), tail[0]);
        Assert.Empty(splitter.Flush());
    }
}

public class PacketCodecTests
{
    private static readonly byte[] Key = RandomNumberGenerator.GetBytes(32);
    private static readonly byte[] Iv = RandomNumberGenerator.GetBytes(16);

    private static async Task<(byte[] Body, bool Quick)> RoundTrip(uint tag, byte[] body)
    {
        var stream = new MemoryStream();
        using (var encrypt = new AesCtr(Key, Iv))
            await stream.WriteAsync(encrypt.Transform(PacketCodec.EncodeReply(body, tag)));
        stream.Position = 0;
        using var decrypt = new AesCtr(Key, Iv);
        return await PacketCodec.ReadPacketAsync(stream, decrypt, tag, CancellationToken.None);
    }

    [Fact]
    public async Task Intermediate_RoundTrips()
    {
        byte[] body = RandomNumberGenerator.GetBytes(64);
        (byte[] parsed, bool quick) = await RoundTrip(MtProtoConstants.ProtoIntermediate, body);
        Assert.Equal(body, parsed);
        Assert.False(quick);
    }

    [Fact]
    public async Task PaddedIntermediate_RoundTrips()
    {
        // 24-byte header plus whole AES blocks: the padded transport cannot express an arbitrary length.
        byte[] body = RandomNumberGenerator.GetBytes(40);
        (byte[] parsed, _) = await RoundTrip(MtProtoConstants.ProtoSecure, body);
        Assert.Equal(body, parsed);
    }

    [Fact]
    public async Task Abridged_RoundTrips()
    {
        byte[] body = RandomNumberGenerator.GetBytes(64);
        (byte[] parsed, _) = await RoundTrip(MtProtoConstants.ProtoAbridged, body);
        Assert.Equal(body, parsed);
    }

    [Fact]
    public void EncodeReply_RejectsUnalignedBodies()
    {
        Assert.Throws<InvalidDataException>(() => PacketCodec.EncodeReply(new byte[7], MtProtoConstants.ProtoIntermediate));
        Assert.Throws<InvalidDataException>(() => PacketCodec.EncodeReply([], MtProtoConstants.ProtoIntermediate));
    }

    [Fact]
    public void EncodeReply_Intermediate_HasTheLittleEndianLength()
    {
        byte[] body = RandomNumberGenerator.GetBytes(32);
        byte[] wire = PacketCodec.EncodeReply(body, MtProtoConstants.ProtoIntermediate);
        Assert.Equal(36, wire.Length);
        Assert.Equal(32, BinaryPrimitives.ReadInt32LittleEndian(wire));
        Assert.Equal(body, wire.AsSpan(4).ToArray());
    }

    [Fact]
    public void EncodeReply_Abridged_UsesWords()
    {
        byte[] body = RandomNumberGenerator.GetBytes(16);
        byte[] wire = PacketCodec.EncodeReply(body, MtProtoConstants.ProtoAbridged);
        Assert.Equal(17, wire.Length);
        Assert.Equal(4, wire[0]);
    }

    [Fact]
    public async Task ReadPacket_RejectsOversizedLengths()
    {
        var stream = new MemoryStream();
        using (var encrypt = new AesCtr(Key, Iv))
        {
            var header = new byte[4];
            BinaryPrimitives.WriteInt32LittleEndian(header, PacketCodec.MaxPacket + 1024);
            await stream.WriteAsync(encrypt.Transform(header));
        }
        stream.Position = 0;
        using var decrypt = new AesCtr(Key, Iv);
        await Assert.ThrowsAsync<InvalidDataException>(() =>
            PacketCodec.ReadPacketAsync(stream, decrypt, MtProtoConstants.ProtoIntermediate, CancellationToken.None));
    }
}
