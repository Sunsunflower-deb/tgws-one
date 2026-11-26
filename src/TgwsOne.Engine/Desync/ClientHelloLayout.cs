using System.Buffers.Binary;
using System.Text;

namespace TgwsOne.Engine.Desync;

/// <summary>
/// A position inside the first flight where a desync step wants to cut, replace or mark a byte.
/// Positions are relative to the parsed ClientHello, not to the socket: the planner resolves them to
/// absolute offsets before anything is sent.
/// </summary>
public abstract record DesyncPosition;

/// <summary>An absolute byte offset in the first flight.</summary>
public sealed record Absolute(int Offset) : DesyncPosition;

/// <summary>The first byte of the SNI host name plus <paramref name="Delta"/>.</summary>
public sealed record SniStart(int Delta = 0) : DesyncPosition;

/// <summary>One past the last byte of the SNI host name plus <paramref name="Delta"/>.</summary>
public sealed record SniEnd(int Delta = 0) : DesyncPosition;

/// <summary>The middle byte of the SNI host name.</summary>
public sealed record SniMid : DesyncPosition;

/// <summary>The middle byte of the second-level domain inside the SNI host name.</summary>
public sealed record SldMid : DesyncPosition;

/// <summary>A byte counted from the end of the first flight: <c>Length - Delta</c>.</summary>
public sealed record FromEnd(int Delta = 0) : DesyncPosition;

/// <summary>
/// The parts of a TLS ClientHello that the desync layer needs: where the first record ends, where the
/// handshake ends and where the SNI host name sits. Parsing is strict on purpose: a hello that cannot be
/// read exactly is not a hello the layer is allowed to touch.
/// </summary>
public sealed class ClientHelloLayout
{
    private readonly int _sniStart;
    private readonly int _sniEnd;
    private readonly int _sldStart;
    private readonly int _sldEnd;

    internal ClientHelloLayout(int length, int recordEnd, int recordVersionMinor, int handshakeEnd,
                               int sniStart, int sniEnd, int sldStart, int sldEnd, string? sni)
    {
        Length = length;
        RecordEnd = recordEnd;
        RecordVersionMinor = recordVersionMinor;
        HandshakeEnd = handshakeEnd;
        _sniStart = sniStart;
        _sniEnd = sniEnd;
        _sldStart = sldStart;
        _sldEnd = sldEnd;
        Sni = sni;
    }

    /// <summary>Number of bytes in the first flight that was parsed.</summary>
    public int Length { get; }

    /// <summary>End of the first TLS record (the one that carries the ClientHello).</summary>
    public int RecordEnd { get; }

    /// <summary>Minor byte of the legacy record version (the <c>0x0X</c> of <c>0x03 0x0X</c>).</summary>
    public int RecordVersionMinor { get; }

    /// <summary>End of the handshake message, which is where the extensions stop.</summary>
    public int HandshakeEnd { get; }

    /// <summary>True when the hello carries a <c>host_name</c> SNI entry.</summary>
    public bool HasSni => _sniStart >= 0;

    /// <summary>The SNI host name, or null when the hello has no usable SNI entry.</summary>
    public string? Sni { get; }

    /// <summary>
    /// Resolves a position to an absolute offset in the first flight, or null when the position does not
    /// apply: an SNI-bound position in a hello without SNI, or an offset outside the buffer.
    /// </summary>
    public int? Resolve(DesyncPosition position)
    {
        int offset;
        switch (position)
        {
            case Absolute absolute:
                offset = absolute.Offset;
                break;
            case SniStart start when HasSni:
                offset = _sniStart + start.Delta;
                break;
            case SniEnd end when HasSni:
                offset = _sniEnd + end.Delta;
                break;
            case SniMid when HasSni:
                offset = (_sniStart + _sniEnd) / 2;
                break;
            case SldMid when HasSni:
                offset = (_sldStart + _sldEnd) / 2;
                break;
            case FromEnd fromEnd:
                offset = Length - fromEnd.Delta;
                break;
            default:
                return null;
        }
        return offset >= 0 && offset <= Length ? offset : null;
    }

    /// <summary>
    /// The layout after a record was cut at <paramref name="split"/> and a second record header was
    /// inserted there. Every byte at or after the cut moves five bytes to the right.
    /// </summary>
    internal ClientHelloLayout WithRecordSplit(int split)
    {
        int Map(int value) => value < split ? value : value + 5;
        return new ClientHelloLayout(
            Length + 5,
            Map(RecordEnd),
            RecordVersionMinor,
            Map(HandshakeEnd),
            HasSni ? Map(_sniStart) : -1,
            HasSni ? Map(_sniEnd) : -1,
            HasSni ? Map(_sldStart) : -1,
            HasSni ? Map(_sldEnd) : -1,
            Sni);
    }

    /// <summary>
    /// Reads the first record, the ClientHello handshake message and the SNI extension. Returns false for
    /// anything that is not a complete ClientHello inside the first record: the desync layer must not
    /// guess at a partial or malformed hello.
    /// </summary>
    public static bool TryParse(ReadOnlySpan<byte> firstFlight, out ClientHelloLayout layout)
    {
        layout = null!;
        if (firstFlight.Length < 5) return false;
        if (firstFlight[0] != 0x16) return false;
        if (firstFlight[1] != 0x03) return false;
        if ((firstFlight[2] & 0xF0) != 0) return false;

        int recordLength = BinaryPrimitives.ReadUInt16BigEndian(firstFlight[3..]);
        if (recordLength < 4) return false;
        int recordEnd = 5 + recordLength;
        if (recordEnd > firstFlight.Length) return false;

        if (firstFlight[5] != 0x01) return false;
        int handshakeLength = (firstFlight[6] << 16) | (firstFlight[7] << 8) | firstFlight[8];
        int handshakeEnd = 9 + handshakeLength;
        // A ClientHello split over several records is deliberately left alone: the layout and all offsets
        // below are defined against a single record.
        if (handshakeEnd > recordEnd) return false;
        if (handshakeEnd > firstFlight.Length) return false;

        int pos = 9;
        if (handshakeEnd - pos < 2 + 32) return false;
        pos += 2 + 32;                                        // client_version, random

        if (!TryReadVector8(firstFlight, ref pos, handshakeEnd)) return false;      // session_id
        if (!TryReadVector16(firstFlight, ref pos, handshakeEnd)) return false;     // cipher_suites
        if (!TryReadVector8(firstFlight, ref pos, handshakeEnd)) return false;      // compression_methods

        int sniStart = -1;
        int sniEnd = -1;
        if (pos < handshakeEnd)
        {
            if (handshakeEnd - pos < 2) return false;
            int extensionsLength = BinaryPrimitives.ReadUInt16BigEndian(firstFlight[pos..]);
            pos += 2;
            if (extensionsLength > handshakeEnd - pos) return false;
            int extensionsEnd = pos + extensionsLength;
            if (extensionsEnd != handshakeEnd) return false;

            while (pos < extensionsEnd)
            {
                if (extensionsEnd - pos < 4) return false;
                int type = BinaryPrimitives.ReadUInt16BigEndian(firstFlight[pos..]);
                int length = BinaryPrimitives.ReadUInt16BigEndian(firstFlight[(pos + 2)..]);
                pos += 4;
                if (length > extensionsEnd - pos) return false;
                int dataStart = pos;
                int dataEnd = pos + length;

                if (type == 0x0000 && sniStart < 0 &&
                    !TryParseServerName(firstFlight, dataStart, dataEnd, out sniStart, out sniEnd))
                {
                    return false;
                }
                pos = dataEnd;
            }
            if (pos != extensionsEnd) return false;
        }

        int sldStart = sniStart;
        int sldEnd = sniEnd;
        if (sniStart >= 0)
        {
            int nameEnd = sniEnd;
            if (nameEnd > sniStart && firstFlight[nameEnd - 1] == (byte)'.') nameEnd--;
            int lastDot = firstFlight[sniStart..nameEnd].LastIndexOf((byte)'.');
            if (lastDot >= 0)
            {
                int previousDot = firstFlight[sniStart..(sniStart + lastDot)].LastIndexOf((byte)'.');
                sldStart = sniStart + previousDot + 1;
                sldEnd = sniStart + lastDot;
            }
        }

        string? sni = sniStart >= 0 ? Encoding.ASCII.GetString(firstFlight[sniStart..sniEnd]) : null;
        layout = new ClientHelloLayout(firstFlight.Length, recordEnd, firstFlight[2], handshakeEnd,
            sniStart, sniEnd, sldStart, sldEnd, sni);
        return true;
    }

    private static bool TryParseServerName(ReadOnlySpan<byte> data, int start, int end,
                                           out int nameStart, out int nameEnd)
    {
        nameStart = -1;
        nameEnd = -1;
        if (end - start < 2) return false;
        int listLength = BinaryPrimitives.ReadUInt16BigEndian(data[start..]);
        if (listLength != end - start - 2) return false;

        int pos = start + 2;
        while (pos < end)
        {
            if (end - pos < 3) return false;
            int nameType = data[pos];
            int length = BinaryPrimitives.ReadUInt16BigEndian(data[(pos + 1)..]);
            pos += 3;
            if (length > end - pos) return false;
            if (nameType == 0 && nameStart < 0)
            {
                if (length == 0) return false;
                nameStart = pos;
                nameEnd = pos + length;
            }
            pos += length;
        }
        return pos == end;
    }

    private static bool TryReadVector8(ReadOnlySpan<byte> data, ref int pos, int end)
    {
        if (pos >= end) return false;
        int length = data[pos++];
        if (length > end - pos) return false;
        pos += length;
        return true;
    }

    private static bool TryReadVector16(ReadOnlySpan<byte> data, ref int pos, int end)
    {
        if (end - pos < 2) return false;
        int length = BinaryPrimitives.ReadUInt16BigEndian(data[pos..]);
        pos += 2;
        if (length > end - pos) return false;
        pos += length;
        return true;
    }
}
