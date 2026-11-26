using System.Buffers.Binary;
using System.Text;

namespace TgwsOne.Engine.Desync;

/// <summary>
/// Builds a plausible TLS 1.3 ClientHello of an exact requested length. ByeDPI sends fake bytes of the same size as
/// the range it replaces, so a DPI that buffers the first segment still sees a well-formed hello; the padding
/// extension (0x0015) absorbs whatever is left after the real extensions are written.
/// </summary>
public static class FakeClientHello
{
    /// <summary>The host ByeDPI's fake strategies use when neither the step nor the real hello names one.</summary>
    public const string DefaultSni = "www.google.com";

    private const byte HandshakeClientHello = 0x01;
    private const byte RecordHandshake = 0x16;
    private const byte RecordVersionMajor = 0x03;
    private const byte RecordVersionMinor = 0x01;
    private const int PaddingExtension = 0x0015;

    private const int LegacyVersionLength = 2;
    private const int RandomLength = 32;
    private const int SessionIdLength = 32;
    private const int RecordHeaderLength = 5;
    private const int HandshakeHeaderLength = 4;

    // Three TLS 1.3 suites are enough to look current; the exact list is not what a DPI fingerprints.
    private static readonly byte[] CipherSuites = [0x13, 0x01, 0x13, 0x02, 0x13, 0x03];
    private static readonly byte[] CompressionMethods = [0x00];
    private static readonly byte[] SupportedVersions = [0x02, 0x03, 0x04];

    /// <summary>Builds a hello with a fresh random seed, so two fakes on the same route are not byte-identical.</summary>
    public static byte[] Build(string? sni, int length)
        => Build(sni, length, unchecked((ulong)Random.Shared.NextInt64()));

    /// <summary>
    /// Builds a hello of exactly <paramref name="length"/> bytes. The seed feeds the internal xorshift that fills
    /// the random, session id and key share, which keeps tests deterministic without pulling in a crypto RNG.
    /// </summary>
    public static byte[] Build(string? sni, int length, ulong seed)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(length);
        if (length > ushort.MaxValue + RecordHeaderLength)
            throw new ArgumentOutOfRangeException(nameof(length), length, "a TLS record cannot be that long");

        string host = string.IsNullOrWhiteSpace(sni) ? DefaultSni : sni;
        byte[] hostBytes = Encoding.ASCII.GetBytes(host);
        if (hostBytes.Length == 0 || hostBytes.Length > ushort.MaxValue)
            throw new ArgumentOutOfRangeException(nameof(sni), "the SNI host name does not fit in the extension");

        var rng = new Xorshift64(seed);
        byte[] random = new byte[RandomLength];
        byte[] sessionId = new byte[SessionIdLength];
        byte[] keyShare = new byte[32];
        rng.NextBytes(random);
        rng.NextBytes(sessionId);
        rng.NextBytes(keyShare);

        // Optional extensions are dropped when the target is too short for them; a fake only has to parse, and the
        // caller decides whether the length is realistic enough to bother sending it at all.
        bool includeSignatureAlgorithms = true;
        bool includeSupportedGroups = true;
        bool includeKeyShare = true;
        List<byte> extensions;
        int total;
        while (true)
        {
            extensions = BuildExtensions(hostBytes, keyShare, includeSignatureAlgorithms,
                includeSupportedGroups, includeKeyShare);
            total = TotalLength(extensions.Count);
            // The padding extension itself needs four header bytes, so only a remainder of four or more can be
            // represented exactly.
            if (length >= total && length - total >= 4) break;
            if (includeSignatureAlgorithms) { includeSignatureAlgorithms = false; continue; }
            if (includeSupportedGroups) { includeSupportedGroups = false; continue; }
            if (includeKeyShare) { includeKeyShare = false; continue; }
            throw new ArgumentOutOfRangeException(nameof(length), length,
                "the requested length cannot be made into a ClientHello");
        }

        AddExtension(extensions, PaddingExtension, new byte[length - total - 4]);

        var body = new List<byte>(length);
        body.Add(0x03);
        body.Add(0x03);
        body.AddRange(random);
        body.Add((byte)sessionId.Length);
        body.AddRange(sessionId);
        AddVector16(body, CipherSuites);
        body.Add((byte)CompressionMethods.Length);
        body.AddRange(CompressionMethods);
        AddVector16(body, [.. extensions]);

        var message = new byte[RecordHeaderLength + HandshakeHeaderLength + body.Count];
        message[0] = RecordHandshake;
        message[1] = RecordVersionMajor;
        message[2] = RecordVersionMinor;
        BinaryPrimitives.WriteUInt16BigEndian(message.AsSpan(3, 2), (ushort)(HandshakeHeaderLength + body.Count));
        message[5] = HandshakeClientHello;
        message[6] = (byte)(body.Count >> 16);
        message[7] = (byte)(body.Count >> 8);
        message[8] = (byte)body.Count;
        body.CopyTo(message, RecordHeaderLength + HandshakeHeaderLength);
        return message;
    }

    private static int TotalLength(int extensionsLength)
    {
        int body = LegacyVersionLength + RandomLength + 1 + SessionIdLength +
                   2 + CipherSuites.Length + 1 + CompressionMethods.Length +
                   2 + extensionsLength;
        return RecordHeaderLength + HandshakeHeaderLength + body;
    }

    private static List<byte> BuildExtensions(byte[] hostBytes, byte[] keyShare,
                                              bool signatureAlgorithms, bool supportedGroups, bool keyShareExtension)
    {
        var extensions = new List<byte>();
        AddServerName(extensions, hostBytes);
        AddExtension(extensions, 0x002b, SupportedVersions);
        if (keyShareExtension) AddKeyShare(extensions, keyShare);
        if (supportedGroups) AddExtension(extensions, 0x000a, [0x00, 0x02, 0x00, 0x1d]);
        if (signatureAlgorithms)
            AddExtension(extensions, 0x000d, [0x00, 0x06, 0x04, 0x03, 0x08, 0x04, 0x04, 0x01]);
        return extensions;
    }

    private static void AddServerName(List<byte> extensions, byte[] host)
    {
        int listLength = host.Length + 3;
        var data = new List<byte>
        {
            (byte)(listLength >> 8), (byte)listLength,
            0x00,
            (byte)(host.Length >> 8), (byte)host.Length,
        };
        data.AddRange(host);
        AddExtension(extensions, 0x0000, [.. data]);
    }

    private static void AddKeyShare(List<byte> extensions, byte[] keyShare)
    {
        // One x25519 share: the client_shares vector is 2 + 2 + 32 bytes long.
        var data = new List<byte>
        {
            0x00, 0x24,
            0x00, 0x1d,
            0x00, 0x20,
        };
        data.AddRange(keyShare);
        AddExtension(extensions, 0x0033, [.. data]);
    }

    private static void AddExtension(List<byte> extensions, int type, byte[] data)
    {
        extensions.Add((byte)(type >> 8));
        extensions.Add((byte)type);
        extensions.Add((byte)(data.Length >> 8));
        extensions.Add((byte)data.Length);
        extensions.AddRange(data);
    }

    private static void AddVector16(List<byte> body, byte[] value)
    {
        body.Add((byte)(value.Length >> 8));
        body.Add((byte)value.Length);
        body.AddRange(value);
    }

    /// <summary>
    /// The smallest usable xorshift64: the plan calls for an injectable seed, and a cryptographic generator would
    /// only add a dependency for bytes that are never trusted as secrets.
    /// </summary>
    private struct Xorshift64
    {
        private ulong _state;

        public Xorshift64(ulong seed)
        {
            // Zero is a fixed point of xorshift, so a zero seed is replaced by the golden-ratio constant.
            _state = seed == 0 ? 0x9E3779B97F4A7C15UL : seed;
        }

        public void NextBytes(Span<byte> destination)
        {
            for (int offset = 0; offset < destination.Length; offset += sizeof(ulong))
            {
                ulong value = Next();
                int count = Math.Min(sizeof(ulong), destination.Length - offset);
                for (int index = 0; index < count; index++)
                    destination[offset + index] = (byte)(value >> (8 * index));
            }
        }

        private ulong Next()
        {
            ulong value = _state;
            value ^= value << 13;
            value ^= value >> 7;
            value ^= value << 17;
            _state = value;
            return value;
        }
    }
}
