using System.Buffers.Binary;
using System.Security.Cryptography;

namespace TgwsOne.Engine.Transport;

/// <summary>What a ClientHello offered: the cipher suites and the key_share groups with their key_exchange sizes.</summary>
public readonly record struct ClientHelloInfo(IReadOnlySet<int> CipherSuites, IReadOnlyDictionary<int, int> KeyShares)
{
    public static readonly ClientHelloInfo Empty =
        new(new HashSet<int>(), new Dictionary<int, int>());
}

/// <summary>The parts of a verified ClientHello the answer is built from.</summary>
public sealed record FakeTlsClientHello(byte[] ClientRandom, byte[] SessionId, uint Timestamp);

/// <summary>
/// The fake-TLS handshake of MTProxy's <c>ee</c> secret, ported from upstream <c>proxy/fake_tls.py</c> (v1.11.4).
/// <para>
/// A client that wants an <c>ee</c> tunnel sends an ordinary TLS 1.3 ClientHello whose <c>random</c> field carries an
/// HMAC of the hello itself and a timestamp; the proxy answers with a ServerHello, ChangeCipherSpec and one
/// ApplicationData record whose own random field is the HMAC over the client random and the whole answer. Both sides
/// then keep the MTProto stream inside ApplicationData records. Nothing here derives real keys: the traffic is
/// obfuscated2 inside, which is why the records only have to look like TLS to a prober.
/// </para>
/// </summary>
public static class FakeTls
{
    public const int RecordHandshake = 0x16;
    public const int RecordCcs = 0x14;
    public const int RecordAlert = 0x15;
    public const int RecordAppData = 0x17;

    /// <summary>Largest payload a single TLS record may carry.</summary>
    public const int MaxRecordPayload = 16384;

    /// <summary>How far the timestamp inside the client random may be from now, in seconds.</summary>
    public const int TimestampTolerance = 120;

    /// <summary>Lowest size of the first ApplicationData record the masking site is known to send.</summary>
    public const int AppDataMin = 64;

    /// <summary>Without a profile stay near the smallest records such a site sends, far from classic MTProxy sizes.</summary>
    public const int AppDataFallbackLow = 64;
    public const int AppDataFallbackHigh = 160;

    private const int ClientRandomOffset = 11;
    private const int ClientRandomLen = 32;
    private const int SessionIdOffset = 44;
    private const int SessionIdLen = 32;

    private const int ExtSupportedVersions = 0x002b;
    private const int ExtKeyShare = 0x0033;

    private const int GroupX25519 = 0x001d;
    private const int GroupX25519MlKem768 = 0x11ec;

    private const int X25519KeyLen = 32;
    private const int MlKemClientShareLen = 1216;
    private const int MlKemCiphertextLen = 1088;

    private const int ServerHelloRandomOffset = 11;

    /// <summary>TLS 1.3 suites in the order a typical server picks them from the client's list.</summary>
    private static readonly int[] CipherPreference = [0x1301, 0x1303, 0x1302];

    private static readonly byte[] ChangeCipherSpec = [0x14, 0x03, 0x03, 0x00, 0x01, 0x01];

    /// <summary>
    /// Checks the HMAC and the timestamp of a ClientHello. The hello is compared with the random field zeroed, so the
    /// check covers the whole message; a hello outside <see cref="TimestampTolerance"/> of <paramref name="nowSeconds"/>
    /// is refused, which is what bounds a replay.
    /// </summary>
    public static bool TryVerifyClientHello(ReadOnlySpan<byte> data, ReadOnlySpan<byte> secret,
                                            out FakeTlsClientHello hello, long? nowSeconds = null)
    {
        hello = null!;
        // 5 (record) + 6 (handshake type, length, version) + 32 (random)
        if (data.Length < 43) return false;
        if (data[0] != RecordHandshake) return false;
        if (data[5] != 0x01) return false;

        byte[] clientRandom = data.Slice(ClientRandomOffset, ClientRandomLen).ToArray();

        byte[] zeroed = data[..Math.Min(data.Length, MaxRecordPayload + 5)].ToArray();
        Array.Clear(zeroed, ClientRandomOffset, ClientRandomLen);

        byte[] expected = HMACSHA256.HashData(secret, zeroed);
        if (!CryptographicOperations.FixedTimeEquals(expected.AsSpan(0, 28), clientRandom.AsSpan(0, 28)))
            return false;

        Span<byte> timestampBytes = stackalloc byte[4];
        for (int i = 0; i < 4; i++) timestampBytes[i] = (byte)(clientRandom[28 + i] ^ expected[28 + i]);
        uint timestamp = BinaryPrimitives.ReadUInt32LittleEndian(timestampBytes);

        long now = nowSeconds ?? DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        if (Math.Abs(now - timestamp) > TimestampTolerance) return false;

        byte[] sessionId = new byte[SessionIdLen];
        if (data.Length >= SessionIdOffset + SessionIdLen && data[43] == 0x20)
            sessionId = data.Slice(SessionIdOffset, SessionIdLen).ToArray();

        hello = new FakeTlsClientHello(clientRandom, sessionId, timestamp);
        return true;
    }

    /// <summary>
    /// Extracts the offered cipher suites and key_share groups. Malformed input returns empty info, so the caller falls
    /// back to the plain X25519 / TLS_AES_128_GCM_SHA256 answer instead of failing the handshake.
    /// </summary>
    public static ClientHelloInfo ParseClientHello(ReadOnlySpan<byte> data)
    {
        try
        {
            int pos = 5 + 4 + 2 + ClientRandomLen;
            if (pos >= data.Length) return ClientHelloInfo.Empty;
            pos += 1 + data[pos];                               // session id

            if (pos + 2 > data.Length) return ClientHelloInfo.Empty;
            int suitesLength = BinaryPrimitives.ReadUInt16BigEndian(data[pos..]);
            int suitesStart = pos + 2;
            pos = suitesStart + suitesLength;
            if (pos > data.Length) return ClientHelloInfo.Empty;

            var suites = new HashSet<int>();
            for (int off = suitesStart; off + 1 < pos; off += 2)
                suites.Add(BinaryPrimitives.ReadUInt16BigEndian(data[off..]));

            if (pos >= data.Length) return ClientHelloInfo.Empty;
            pos += 1 + data[pos];                               // compression methods

            if (pos + 2 > data.Length) return new ClientHelloInfo(suites, new Dictionary<int, int>());
            int extensionsLength = BinaryPrimitives.ReadUInt16BigEndian(data[pos..]);
            pos += 2;
            int extensionsEnd = Math.Min(pos + extensionsLength, data.Length);

            var keyShares = new Dictionary<int, int>();
            while (pos + 4 <= extensionsEnd)
            {
                int extensionType = BinaryPrimitives.ReadUInt16BigEndian(data[pos..]);
                int extensionLength = BinaryPrimitives.ReadUInt16BigEndian(data[(pos + 2)..]);
                pos += 4;
                if (extensionType == ExtKeyShare && pos + 2 <= extensionsEnd)
                {
                    int sharesEnd = Math.Min(pos + 2 + BinaryPrimitives.ReadUInt16BigEndian(data[pos..]), extensionsEnd);
                    for (int off = pos + 2; off + 4 <= sharesEnd;)
                    {
                        int group = BinaryPrimitives.ReadUInt16BigEndian(data[off..]);
                        int keyLength = BinaryPrimitives.ReadUInt16BigEndian(data[(off + 2)..]);
                        off += 4 + keyLength;
                        if (off <= sharesEnd) keyShares.TryAdd(group, keyLength);
                    }
                }
                pos += extensionLength;
            }
            return new ClientHelloInfo(suites, keyShares);
        }
        catch (ArgumentOutOfRangeException)
        {
            return ClientHelloInfo.Empty;
        }
    }

    /// <summary>
    /// The answer a client expects: ServerHello, ChangeCipherSpec and one ApplicationData record, with the ServerHello
    /// random replaced by the HMAC over the client random and the rest of the answer. Telegram parses exactly these
    /// three records and verifies that HMAC, so the shape is fixed while the record size may vary.
    /// </summary>
    public static byte[] BuildResponse(ReadOnlySpan<byte> secret, ReadOnlySpan<byte> clientRandom,
                                       ReadOnlySpan<byte> sessionId, ClientHelloInfo info, int? appDataLength = null)
    {
        (int group, byte[] keyExchange) = ServerKeyShare(info);
        byte[] extensions =
        [
            0x00, 0x2b, 0x00, 0x02, 0x03, 0x04,             // supported_versions: TLS 1.3
            .. KeyShareExtension(group, keyExchange),
        ];

        int cipherSuite = SelectCipherSuite(info);

        // ServerHello body: version, 32 bytes random (filled in below), session id, suite, compression, extensions.
        int bodyLength = 2 + 32 + 1 + sessionId.Length + 2 + 1 + 2 + extensions.Length;
        int messageLength = 1 + 3 + bodyLength;                 // handshake type + 3-byte length + body
        var response = new List<byte>(5 + messageLength + ChangeCipherSpec.Length + 5 + AppDataMax(appDataLength));

        response.AddRange([0x16, 0x03, 0x03, (byte)(messageLength >> 8), (byte)messageLength]);
        response.Add(0x02);                                     // ClientHello answer
        response.AddRange([(byte)(bodyLength >> 16), (byte)(bodyLength >> 8), (byte)bodyLength]);
        response.AddRange([0x03, 0x03]);                        // negotiated TLS 1.2 legacy version
        response.AddRange(new byte[32]);                        // server random, replaced at the end
        response.Add((byte)sessionId.Length);
        response.AddRange(sessionId);
        response.AddRange([(byte)(cipherSuite >> 8), (byte)cipherSuite]);
        response.Add(0x00);                                     // no compression
        response.AddRange([(byte)(extensions.Length >> 8), (byte)extensions.Length]);
        response.AddRange(extensions);

        response.AddRange(ChangeCipherSpec);

        int appDataSize = AppDataLength(appDataLength);
        response.AddRange([0x17, 0x03, 0x03, (byte)(appDataSize >> 8), (byte)appDataSize]);
        byte[] filler = RandomNumberGenerator.GetBytes(appDataSize);
        response.AddRange(filler);

        byte[] answer = [.. response];
        byte[] serverRandom = HMACSHA256.HashData(secret, [.. clientRandom, .. answer]);
        serverRandom.CopyTo(answer, ServerHelloRandomOffset);
        return answer;
    }

    /// <summary>A TLS alert record, so a refused connection still looks like a TLS endpoint that said no.</summary>
    public static byte[] Alert(byte description) => [0x15, 0x03, 0x03, 0x00, 0x02, 0x02, description];

    /// <summary><c>handshake_failure</c>: what a server sends when it cannot continue the handshake.</summary>
    public const byte AlertHandshakeFailure = 0x28;

    /// <summary><c>unexpected_message</c>: what a server sends when the first bytes are not a valid ClientHello.</summary>
    public const byte AlertUnexpectedMessage = 0x0a;

    /// <summary>The record header of a TLS record: type, version and a two-byte payload length.</summary>
    public static bool TryReadRecordLength(ReadOnlySpan<byte> header, out int payloadLength)
    {
        payloadLength = 0;
        if (header.Length < 5) return false;
        if (header[1] != 0x03) return false;
        payloadLength = BinaryPrimitives.ReadUInt16BigEndian(header[3..]);
        return true;
    }

    /// <summary>
    /// Reads one whole TLS record: a five-byte header and its payload. False when the peer closed, the header is not a
    /// record, or the payload is larger than <paramref name="maxPayload"/>.
    /// </summary>
    public static async Task<(bool Ok, byte[] Record)> TryReadRecordAsync(Stream stream, int maxPayload, CancellationToken ct)
    {
        byte[] header = new byte[5];
        if (!await ReadExactlyAsync(stream, header, ct).ConfigureAwait(false)) return (false, []);
        if (!TryReadRecordLength(header, out int payloadLength) || payloadLength > maxPayload) return (false, []);

        byte[] record = new byte[5 + payloadLength];
        header.CopyTo(record, 0);
        if (payloadLength > 0 && !await ReadExactlyAsync(stream, record.AsMemory(5, payloadLength), ct).ConfigureAwait(false))
            return (false, []);
        return (true, record);
    }

    private static async Task<bool> ReadExactlyAsync(Stream stream, Memory<byte> buffer, CancellationToken ct)
    {
        int read = 0;
        while (read < buffer.Length)
        {
            int n = await stream.ReadAsync(buffer[read..], ct).ConfigureAwait(false);
            if (n == 0) return false;
            read += n;
        }
        return true;
    }

    private static byte[] KeyShareExtension(int group, byte[] keyExchange)
    {
        int dataLength = 4 + keyExchange.Length;                 // group + key_exchange length + key
        return
        [
            0x00, 0x33,
            (byte)(dataLength >> 8), (byte)dataLength,
            (byte)(group >> 8), (byte)group,
            (byte)(keyExchange.Length >> 8), (byte)keyExchange.Length,
            .. keyExchange,
        ];
    }

    private static int SelectCipherSuite(ClientHelloInfo info)
    {
        foreach (int suite in CipherPreference)
            if (info.CipherSuites.Contains(suite)) return suite;
        return CipherPreference[0];
    }

    /// <summary>
    /// Mirrors the hybrid post-quantum group when the client offers it, the way current browsers and servers do. The
    /// client never derives keys from this exchange; the bytes only have to look right.
    /// </summary>
    private static (int Group, byte[] KeyExchange) ServerKeyShare(ClientHelloInfo info)
        => info.KeyShares.TryGetValue(GroupX25519MlKem768, out int shareLength) && shareLength == MlKemClientShareLen
            ? (GroupX25519MlKem768, [.. RandomNumberGenerator.GetBytes(MlKemCiphertextLen), .. X25519PublicKey()])
            : (GroupX25519, X25519PublicKey());

    /// <summary>
    /// A public key shaped like a real X25519 one: field elements are below 2^255, so the top bit stays clear. Nothing
    /// is derived from it, and .NET exposes no X25519 key generation to borrow.
    /// </summary>
    private static byte[] X25519PublicKey()
    {
        byte[] key = RandomNumberGenerator.GetBytes(X25519KeyLen);
        key[^1] &= 0x7f;
        return key;
    }

    private static int AppDataLength(int? appDataLength)
        => appDataLength is { } size
            ? Math.Clamp(size, AppDataMin, MaxRecordPayload)
            : Random.Shared.Next(AppDataFallbackLow, AppDataFallbackHigh + 1);

    private static int AppDataMax(int? appDataLength)
        => appDataLength is { } size ? Math.Clamp(size, AppDataMin, MaxRecordPayload) : AppDataFallbackHigh;
}

/// <summary>
/// A stream whose bytes travel inside TLS ApplicationData records, as the MTProto tunnel of an <c>ee</c> secret does.
/// ChangeCipherSpec records are skipped when reading; anything that is not ApplicationData ends the stream. Writes are
/// split into records of at most <see cref="FakeTls.MaxRecordPayload"/>.
/// </summary>
public sealed class FakeTlsStream : Stream
{
    private readonly Stream _inner;
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private readonly byte[] _header = new byte[5];
    private int _recordRemaining;

    public FakeTlsStream(Stream inner) => _inner = inner;

    public override bool CanRead => true;
    public override bool CanSeek => false;
    public override bool CanWrite => true;
    public override long Length => throw new NotSupportedException();
    public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

    public override int Read(byte[] buffer, int offset, int count)
        => ReadAsync(buffer.AsMemory(offset, count), CancellationToken.None).AsTask().GetAwaiter().GetResult();

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
    {
        if (buffer.Length == 0) return 0;

        while (true)
        {
            if (_recordRemaining > 0)
            {
                int want = Math.Min(buffer.Length, _recordRemaining);
                int read = await _inner.ReadAsync(buffer[..want], ct).ConfigureAwait(false);
                if (read == 0) return 0;
                _recordRemaining -= read;
                return read;
            }

            if (!await ReadHeaderAsync(ct).ConfigureAwait(false)) return 0;
            int type = _header[0];
            if (type == FakeTls.RecordCcs)
            {
                // ChangeCipherSpec carries one byte and means nothing to the tunnel: skip it.
                await SkipPayloadAsync(ct).ConfigureAwait(false);
                continue;
            }
            if (type != FakeTls.RecordAppData) return 0;        // alert or plain handshake: the peer is not tunneling

            _recordRemaining = (int)BinaryPrimitives.ReadUInt16BigEndian(_header.AsSpan(3));
            if (_recordRemaining == 0) continue;
        }
    }

    private async Task<bool> ReadHeaderAsync(CancellationToken ct)
    {
        int read = 0;
        while (read < _header.Length)
        {
            int n = await _inner.ReadAsync(_header.AsMemory(read), ct).ConfigureAwait(false);
            if (n == 0) return false;
            read += n;
        }
        return true;
    }

    private async Task SkipPayloadAsync(CancellationToken ct)
    {
        int left = BinaryPrimitives.ReadUInt16BigEndian(_header.AsSpan(3));
        var scratch = new byte[Math.Min(left, 256)];
        while (left > 0)
        {
            int read = await _inner.ReadAsync(scratch.AsMemory(0, Math.Min(scratch.Length, left)), ct).ConfigureAwait(false);
            if (read == 0) return;
            left -= read;
        }
    }

    public override void Write(byte[] buffer, int offset, int count)
        => WriteAsync(buffer.AsMemory(offset, count), CancellationToken.None).AsTask().GetAwaiter().GetResult();

    public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken ct = default)
    {
        if (buffer.Length == 0) return;

        await _writeLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            int offset = 0;
            while (offset < buffer.Length)
            {
                int chunk = Math.Min(FakeTls.MaxRecordPayload, buffer.Length - offset);
                var header = new byte[5];
                header[0] = FakeTls.RecordAppData;
                header[1] = 0x03;
                header[2] = 0x03;
                BinaryPrimitives.WriteUInt16BigEndian(header.AsSpan(3), (ushort)chunk);

                await _inner.WriteAsync(header, ct).ConfigureAwait(false);
                await _inner.WriteAsync(buffer.Slice(offset, chunk), ct).ConfigureAwait(false);
                offset += chunk;
            }
            await _inner.FlushAsync(ct).ConfigureAwait(false);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    public override void Flush() => _inner.Flush();
    public override Task FlushAsync(CancellationToken ct) => _inner.FlushAsync(ct);
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();

    protected override void Dispose(bool disposing)
    {
        if (disposing) _writeLock.Dispose();
        base.Dispose(disposing);
    }
}
