using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using TgwsOne.Engine.Transport;

namespace TgwsOne.Engine.Tests;

/// <summary>
/// The fake-TLS handshake is checked from both sides here: the client hello is built exactly as a client builds it
/// (HMAC and timestamp in the random field), and the proxy's answer is verified the way Telegram verifies it.
/// </summary>
public class FakeTlsTests
{
    private static readonly byte[] Secret = Convert.FromHexString("00b42be1ae20f9e3b5d6e543cc4bce98");

    private const uint Now = 1_700_000_000;

    // ---------- a client hello, built the way a client builds it ----------

    /// <summary>A structurally valid TLS 1.3 ClientHello with the given random field, session id and key share.</summary>
    private static byte[] Hello(byte[] random, byte[] keyShare, bool hybridKeyShare = false)
    {
        int group = hybridKeyShare ? 0x11ec : 0x001d;

        var body = new List<byte> { 0x03, 0x03 };                 // legacy version
        body.AddRange(random);
        body.Add(0x20);                                           // session id length: 32 bytes
        body.AddRange(Enumerable.Repeat((byte)0x11, 32));         // session id
        body.AddRange([0x00, 0x06, 0x13, 0x01, 0x13, 0x02, 0x13, 0x03]);   // cipher suites
        body.AddRange([0x01, 0x00]);                              // one compression method: null

        var share = new List<byte>();
        share.AddRange([(byte)(group >> 8), (byte)group]);
        share.AddRange([(byte)(keyShare.Length >> 8), (byte)keyShare.Length]);
        share.AddRange(keyShare);
        // ClientHello key_share data is a client_shares vector: its own two-byte length, then the shares.
        int sharesLength = share.Count;
        var keyShareExtension = new List<byte>
        {
            0x00, 0x33,
            (byte)((sharesLength + 2) >> 8), (byte)(sharesLength + 2),
            (byte)(sharesLength >> 8), (byte)sharesLength,
        };
        keyShareExtension.AddRange(share);

        body.AddRange([(byte)(keyShareExtension.Count >> 8), (byte)keyShareExtension.Count]);
        body.AddRange(keyShareExtension);

        var message = new List<byte> { 0x01 };
        message.AddRange([(byte)(body.Count >> 16), (byte)(body.Count >> 8), (byte)body.Count]);
        message.AddRange(body);

        var record = new List<byte> { 0x16, 0x03, 0x01, (byte)(message.Count >> 8), (byte)message.Count };
        record.AddRange(message);
        return [.. record];
    }

    // A key share of a fixed shape: the HMAC is computed over the whole hello, so both passes must build the same bytes.
    private static byte[] FixedKeyShare(bool hybrid) => Enumerable.Repeat((byte)0x22, hybrid ? 1216 : 32).ToArray();

    /// <summary>The hello a real client sends: the random field carries the HMAC and the timestamp.</summary>
    private static byte[] SignedHello(byte[] secret, uint timestamp, bool hybridKeyShare = false)
    {
        byte[] keyShare = FixedKeyShare(hybridKeyShare);
        byte[] zeroed = Hello(new byte[32], keyShare, hybridKeyShare);
        byte[] expected = HMACSHA256.HashData(secret, zeroed);

        byte[] random = new byte[32];
        expected.AsSpan(0, 28).CopyTo(random);
        for (int i = 0; i < 4; i++)
            random[28 + i] = (byte)(expected[28 + i] ^ (byte)((timestamp >> (8 * i)) & 0xff));

        return Hello(random, keyShare, hybridKeyShare);
    }

    // ---------- verification ----------

    [Fact]
    public void TryVerifyClientHello_AcceptsAHelloSignedWithTheSecret()
    {
        byte[] hello = SignedHello(Secret, Now);

        Assert.True(FakeTls.TryVerifyClientHello(hello, Secret, out FakeTlsClientHello parsed, Now));
        Assert.Equal(Now, parsed.Timestamp);
        Assert.Equal(32, parsed.SessionId.Length);
        Assert.Equal(hello[11..43], parsed.ClientRandom);
    }

    [Fact]
    public void TryVerifyClientHello_RefusesAnotherSecret()
    {
        byte[] hello = SignedHello(Secret, Now);
        byte[] other = Convert.FromHexString("ffeeddccbbaa99887766554433221100");

        Assert.False(FakeTls.TryVerifyClientHello(hello, other, out _, Now));
    }

    [Fact]
    public void TryVerifyClientHello_RefusesATimestampOutsideTheWindow()
    {
        // A captured hello replayed an hour later must not open a tunnel: that window is the whole replay defence.
        byte[] hello = SignedHello(Secret, Now);

        Assert.True(FakeTls.TryVerifyClientHello(hello, Secret, out _, Now + FakeTls.TimestampTolerance));
        Assert.False(FakeTls.TryVerifyClientHello(hello, Secret, out _, Now + FakeTls.TimestampTolerance + 1));
        Assert.False(FakeTls.TryVerifyClientHello(hello, Secret, out _, Now - FakeTls.TimestampTolerance - 1));
    }

    [Theory]
    [InlineData(0)]   // not a handshake record
    [InlineData(5)]   // not a ClientHello
    public void TryVerifyClientHello_RefusesSomethingThatIsNotAClientHello(int offsetToCorrupt)
    {
        byte[] hello = SignedHello(Secret, Now);
        hello[offsetToCorrupt] = 0x42;

        Assert.False(FakeTls.TryVerifyClientHello(hello, Secret, out _, Now));
    }

    [Fact]
    public void TryVerifyClientHello_RefusesAShortRecord()
        => Assert.False(FakeTls.TryVerifyClientHello([0x16, 0x03, 0x01, 0x00, 0x02, 0x01, 0x00], Secret, out _, Now));

    // ---------- the answer ----------

    [Fact]
    public void BuildResponse_IsVerifiableTheWayTheClientVerifiesIt()
    {
        byte[] hello = SignedHello(Secret, Now);
        Assert.True(FakeTls.TryVerifyClientHello(hello, Secret, out FakeTlsClientHello parsed, Now));

        byte[] answer = FakeTls.BuildResponse(Secret, parsed.ClientRandom, parsed.SessionId, FakeTls.ParseClientHello(hello));

        // The client zeroes the server random, hashes client_random + the whole answer and expects that value in it.
        byte[] zeroed = [.. answer];
        Array.Clear(zeroed, 11, 32);
        byte[] hmacInput = [.. parsed.ClientRandom, .. zeroed];
        byte[] expected = HMACSHA256.HashData(Secret, hmacInput);

        Assert.Equal(expected, answer[11..43]);
    }

    [Fact]
    public void BuildResponse_KeepsTheThreeRecordShape()
    {
        byte[] hello = SignedHello(Secret, Now);
        Assert.True(FakeTls.TryVerifyClientHello(hello, Secret, out FakeTlsClientHello parsed, Now));

        byte[] answer = FakeTls.BuildResponse(Secret, parsed.ClientRandom, parsed.SessionId, FakeTls.ParseClientHello(hello), appDataLength: 200);

        // ServerHello, then ChangeCipherSpec, then one ApplicationData: Telegram parses exactly these three.
        Assert.Equal(FakeTls.RecordHandshake, answer[0]);
        int serverHelloLength = 5 + BinaryPrimitives.ReadUInt16BigEndian(answer.AsSpan(3));
        Assert.Equal(FakeTls.RecordCcs, answer[serverHelloLength]);
        int ccsLength = 5 + BinaryPrimitives.ReadUInt16BigEndian(answer.AsSpan(serverHelloLength + 3));
        int appDataOffset = serverHelloLength + ccsLength;
        Assert.Equal(FakeTls.RecordAppData, answer[appDataOffset]);
        Assert.Equal(200, BinaryPrimitives.ReadUInt16BigEndian(answer.AsSpan(appDataOffset + 3)));
        Assert.Equal(appDataOffset + 5 + 200, answer.Length);
    }

    [Fact]
    public void BuildResponse_SessionIdIsEchoed_AndTls13SuiteIsChosen()
    {
        byte[] hello = SignedHello(Secret, Now);
        Assert.True(FakeTls.TryVerifyClientHello(hello, Secret, out FakeTlsClientHello parsed, Now));

        byte[] answer = FakeTls.BuildResponse(Secret, parsed.ClientRandom, parsed.SessionId, FakeTls.ParseClientHello(hello));

        // body: version(2) random(32) session id (1 + n) suite(2) compression(1) extensions length(2)
        int bodyStart = 9;
        Assert.Equal(0x20, answer[bodyStart + 34]);
        Assert.Equal(parsed.SessionId, answer[(bodyStart + 35)..(bodyStart + 35 + 32)]);
        int suiteOffset = bodyStart + 35 + 32;
        Assert.Equal(0x1301, BinaryPrimitives.ReadUInt16BigEndian(answer.AsSpan(suiteOffset)));
    }

    [Fact]
    public void BuildResponse_MirrorsTheHybridPostQuantumGroup()
    {
        byte[] hello = SignedHello(Secret, Now, hybridKeyShare: true);
        Assert.True(FakeTls.TryVerifyClientHello(hello, Secret, out FakeTlsClientHello parsed, Now));
        ClientHelloInfo info = FakeTls.ParseClientHello(hello);
        Assert.Equal(1216, info.KeyShares[0x11ec]);

        byte[] answer = FakeTls.BuildResponse(Secret, parsed.ClientRandom, parsed.SessionId, info);

        // Walk the ServerHello extensions to the key_share one, rather than searching for byte patterns that a
        // random field could contain by chance.
        int bodyStart = 9;                                        // record(5) + handshake type(1) + length(3)
        int sessionIdLength = answer[bodyStart + 34];
        int extensionsLengthOffset = bodyStart + 2 + 32 + 1 + sessionIdLength + 2 + 1;
        int extensionsLength = BinaryPrimitives.ReadUInt16BigEndian(answer.AsSpan(extensionsLengthOffset));
        int offset = extensionsLengthOffset + 2;
        int end = offset + extensionsLength;
        int? group = null;
        while (offset + 4 <= end)
        {
            int extensionType = BinaryPrimitives.ReadUInt16BigEndian(answer.AsSpan(offset));
            int extensionLength = BinaryPrimitives.ReadUInt16BigEndian(answer.AsSpan(offset + 2));
            if (extensionType == 0x0033) group = BinaryPrimitives.ReadUInt16BigEndian(answer.AsSpan(offset + 4));
            offset += 4 + extensionLength;
        }

        Assert.Equal(0x11ec, group);
    }

    [Fact]
    public void BuildResponse_WithoutAProfileStaysNearTheMaskedSite()
    {
        byte[] hello = SignedHello(Secret, Now);
        Assert.True(FakeTls.TryVerifyClientHello(hello, Secret, out FakeTlsClientHello parsed, Now));
        ClientHelloInfo info = FakeTls.ParseClientHello(hello);

        for (int i = 0; i < 20; i++)
        {
            byte[] answer = FakeTls.BuildResponse(Secret, parsed.ClientRandom, parsed.SessionId, info);
            int serverHelloLength = 5 + BinaryPrimitives.ReadUInt16BigEndian(answer.AsSpan(3));
            int ccsLength = 5 + BinaryPrimitives.ReadUInt16BigEndian(answer.AsSpan(serverHelloLength + 3));
            int appDataOffset = serverHelloLength + ccsLength;
            int appDataLength = BinaryPrimitives.ReadUInt16BigEndian(answer.AsSpan(appDataOffset + 3));

            Assert.InRange(appDataLength, FakeTls.AppDataFallbackLow, FakeTls.AppDataFallbackHigh);
        }
    }

    // ---------- the tunnel ----------

    [Fact]
    public async Task FakeTlsStream_WrapsWritesInApplicationDataRecords()
    {
        using var inner = new MemoryStream();
        await using (var tunnel = new FakeTlsStream(inner))
            await tunnel.WriteAsync(new byte[] { 1, 2, 3, 4, 5 });

        byte[] written = inner.ToArray();
        Assert.Equal(FakeTls.RecordAppData, written[0]);
        Assert.Equal(5, BinaryPrimitives.ReadUInt16BigEndian(written.AsSpan(3)));
        Assert.Equal(new byte[] { 1, 2, 3, 4, 5 }, written[5..]);
    }

    [Fact]
    public async Task FakeTlsStream_SplitsLargeWritesIntoRecords()
    {
        using var inner = new MemoryStream();
        byte[] payload = RandomNumberGenerator.GetBytes(40_000);
        await using (var tunnel = new FakeTlsStream(inner))
            await tunnel.WriteAsync(payload);

        byte[] written = inner.ToArray();
        int offset = 0;
        var reassembled = new List<byte>();
        while (offset < written.Length)
        {
            Assert.Equal(FakeTls.RecordAppData, written[offset]);
            int length = BinaryPrimitives.ReadUInt16BigEndian(written.AsSpan(offset + 3));
            Assert.InRange(length, 1, FakeTls.MaxRecordPayload);
            reassembled.AddRange(written[(offset + 5)..(offset + 5 + length)]);
            offset += 5 + length;
        }
        Assert.Equal(payload, reassembled);
    }

    [Fact]
    public async Task FakeTlsStream_ReadsApplicationDataAndSkipsChangeCipherSpec()
    {
        // CCS first, then the payload: the tunnel must not treat the one-byte record as data.
        byte[] framed =
        [
            0x14, 0x03, 0x03, 0x00, 0x01, 0x01,                 // ChangeCipherSpec
            0x17, 0x03, 0x03, 0x00, 0x04, 0xDE, 0xAD, 0xBE, 0xEF,
        ];
        using var inner = new MemoryStream(framed);
        await using var tunnel = new FakeTlsStream(inner);

        var buffer = new byte[4];
        int read = await tunnel.ReadAsync(buffer);

        Assert.Equal(4, read);
        Assert.Equal(new byte[] { 0xDE, 0xAD, 0xBE, 0xEF }, buffer);
    }

    [Fact]
    public async Task FakeTlsStream_EndsOnSomethingThatIsNotApplicationData()
    {
        using var inner = new MemoryStream(FakeTls.Alert(FakeTls.AlertHandshakeFailure));
        await using var tunnel = new FakeTlsStream(inner);

        Assert.Equal(0, await tunnel.ReadAsync(new byte[16]));
    }

    [Fact]
    public async Task FakeTlsStream_RoundTripsItsOwnFraming()
    {
        byte[] payload = RandomNumberGenerator.GetBytes(5000);
        using var inner = new MemoryStream();
        await using (var writer = new FakeTlsStream(inner))
            await writer.WriteAsync(payload);

        inner.Position = 0;
        await using var reader = new FakeTlsStream(inner);
        var read = new byte[payload.Length];
        int got = 0;
        while (got < read.Length)
        {
            int n = await reader.ReadAsync(read.AsMemory(got));
            Assert.True(n > 0, "the tunnel ended early");
            got += n;
        }
        Assert.Equal(payload, read);
    }

    // ---------- the alert ----------

    [Fact]
    public void Alert_IsAFatalAlertRecord()
    {
        Assert.Equal(new byte[] { 0x15, 0x03, 0x03, 0x00, 0x02, 0x02, FakeTls.AlertHandshakeFailure },
                     FakeTls.Alert(FakeTls.AlertHandshakeFailure));
    }

    // ---------- keep-alive ----------

    [Fact]
    public void NextPingDelay_WithoutJitterIsExactlyTheInterval()
        => Assert.Equal(TimeSpan.FromSeconds(25), RawWebSocket.NextPingDelay(25, 0, new Random(1)));

    [Fact]
    public void NextPingDelay_StaysInsideTheJitterWindow_AndVaries()
    {
        var rng = new Random(12345);
        var seen = new HashSet<int>();
        for (int i = 0; i < 200; i++)
        {
            TimeSpan delay = RawWebSocket.NextPingDelay(25, 10, rng);
            Assert.InRange(delay, TimeSpan.FromSeconds(25), TimeSpan.FromSeconds(35));
            seen.Add((int)delay.TotalMilliseconds);
        }
        Assert.True(seen.Count > 10, "the jitter must not be one constant");
    }

    [Fact]
    public void NextPingDelay_ClampsAnIntervalBelowOneSecond()
        => Assert.Equal(TimeSpan.FromSeconds(1), RawWebSocket.NextPingDelay(0, 0, new Random(2)));

    // ---------- the link ----------

    [Fact]
    public void TelegramLink_CarriesAnEeSecret_WithTheMaskedDomainInHex()
    {
        var config = new EngineConfig { Secret = "00b42be1ae20f9e3b5d6e543cc4bce98", FakeTlsDomain = "www.google.com" };

        string link = config.TelegramLink();

        string expectedDomain = Convert.ToHexStringLower(Encoding.ASCII.GetBytes("www.google.com"));
        Assert.EndsWith($"secret=ee00b42be1ae20f9e3b5d6e543cc4bce98{expectedDomain}", link);
    }

    [Fact]
    public void TelegramLink_WithoutAMaskingDomainStaysADdSecret()
    {
        var config = new EngineConfig { Secret = "00b42be1ae20f9e3b5d6e543cc4bce98" };

        Assert.Contains("secret=dd00b42be1ae20f9e3b5d6e543cc4bce98", config.TelegramLink());
    }
}
