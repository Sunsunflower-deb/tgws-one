using System.Text.Json;
using TgwsOne.Engine.Crypto;

namespace TgwsOne.Engine.Tests;

/// <summary>
/// Fixed vectors made outside the engine: the handshake and the stream keys as Python's <c>cryptography</c> library
/// computes them, from the upstream code (<c>tools/obfuscated2-vectors.py</c>). The other tests of the handshake
/// build both sides with the engine's own primitives, so they cannot see a mistake that is the same on both sides.
/// </summary>
public class Obfuscated2ReferenceTests
{
    private static readonly JsonSerializerOptions Json = new() { PropertyNameCaseInsensitive = true };
    private static readonly Reference Fixture = JsonSerializer.Deserialize<Reference>(
        File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Fixtures", "obfuscated2-reference.json")), Json)!;

    public static IEnumerable<object[]> EveryCase()
        => Enumerable.Range(0, Fixture.Cases.Count).Select(i => new object[] { i });

    [Theory]
    [MemberData(nameof(EveryCase))]
    public void HandshakeAndBothDirections_MatchPython(int index)
    {
        ReferenceCase c = Fixture.Cases[index];
        byte[] secret = Convert.FromHexString(c.Secret);
        byte[] relayInit = Convert.FromHexString(c.RelayInit);

        bool parsed = Obfuscated2.TryParseClientHandshake(Convert.FromHexString(c.Handshake), secret,
            out int dc, out bool isMedia, out uint protoTag, out byte[] prekeyIv);

        Assert.True(parsed, "Python accepts this handshake");
        Assert.Equal((c.Expect.Dc, c.Expect.IsMedia, c.Expect.ProtoTag), (dc, isMedia, protoTag));

        using var ctx = new CryptoContext(prekeyIv, secret, relayInit);
        Assert.Equal(Convert.FromHexString(c.UpWire), ReEncrypt(ctx.CltDec, ctx.TgEnc, c.Up, c.UpReads));
        Assert.Equal(Convert.FromHexString(c.DownWire), ReEncrypt(ctx.TgDec, ctx.CltEnc, c.Down, c.DownReads));
    }

    [Theory]
    [MemberData(nameof(EveryCase))]
    public void RelayInit_MatchesPython(int index)
    {
        ReferenceCase c = Fixture.Cases[index];

        byte[] relayInit = Obfuscated2.SealRelayInit(
            Convert.FromHexString(c.RelayRandom), c.Expect.ProtoTag, (short)c.DcIdx);

        Assert.Equal(Convert.FromHexString(c.RelayInit), relayInit);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    public void AHandshakePythonRejects_IsRejectedHere(int index)
    {
        ReferenceReject r = Fixture.Rejects[index];

        Assert.False(Obfuscated2.TryParseClientHandshake(
            Convert.FromHexString(r.Handshake), Convert.FromHexString(r.Secret), out _, out _, out _, out _));
    }

    /// <summary>The wire bytes of one direction when the reads arrive as recorded: decrypt with one key, encrypt with the other.</summary>
    private static byte[] ReEncrypt(AesCtr first, AesCtr second, string hex, int[] reads)
    {
        byte[] plain = Convert.FromHexString(hex);
        var wire = new byte[plain.Length];
        int offset = 0;
        foreach (int count in reads)
        {
            var middle = new byte[count];
            first.Transform(plain.AsSpan(offset, count), middle);
            second.Transform(middle, wire.AsSpan(offset));
            offset += count;
        }
        Assert.Equal(plain.Length, offset);
        return wire;
    }

    private sealed record Reference(string Source, List<ReferenceCase> Cases, List<ReferenceReject> Rejects);
    private sealed record ReferenceExpect(int Dc, bool IsMedia, uint ProtoTag);
    private sealed record ReferenceReject(string Secret, string Handshake);
    private sealed record ReferenceCase(int DcIdx, string Secret, string Handshake, string RelayRandom, string RelayInit,
        ReferenceExpect Expect, string Up, int[] UpReads, string UpWire, string Down, int[] DownReads, string DownWire);
}
