// Record driver shim for rust-anything (RT6).  Wave 4 has three units whose C#
// signatures the bridge cannot take: MsgSplitter is an instance with
// ReadOnlySpan parameters, PacketCodec reads a Stream and uses an AesCtr, and
// FakeTls takes spans plus an RNG.  This adapter feeds the same inputs through
// the original code and returns hex strings, so the generated fixture is
// recorded from the C# behaviour rather than reimplemented here.
//
// Not part of TgwsOne.slnx and not compiled into any product assembly.
using TgwsOne.Engine.Crypto;
using TgwsOne.Engine.MtProto;

namespace TgwsOne.Engine.MtProto
{
    public static class MsgSplitterShims
    {
        public sealed record SplitRun(string[] Parts, string[] Flush, string? Error);

        public static SplitRun Run(uint proto, string[] cipherChunks, string[] plainChunks)
        {
            var splitter = new MsgSplitter(proto);
            var parts = new List<string>();
            try
            {
                for (int i = 0; i < cipherChunks.Length; i++)
                {
                    byte[] cipher = Convert.FromHexString(cipherChunks[i]);
                    byte[] plain = Convert.FromHexString(plainChunks[i]);
                    foreach (byte[] part in splitter.Split(cipher, plain))
                        parts.Add(Convert.ToHexStringLower(part));
                }
                string[] flush = [.. splitter.Flush().Select(Convert.ToHexStringLower)];
                return new(parts.ToArray(), flush, null);
            }
            catch (Exception exception)
            {
                return new(parts.ToArray(), [], exception.Message);
            }
        }
    }

    public static class PacketCodecShims
    {
        public sealed record DecodeResult(string Wire, string? Body, bool QuickAck, string? Error);

        // frameHex is the decrypted stream content (length prefix plus body).
        // The shim encrypts it with the original AesCtr, then lets the original
        // ReadPacketAsync decrypt and parse it, and returns both the wire and
        // the body so the fixture can replay the whole path.
        public static DecodeResult Decode(
            string frameHex, string keyHex, string ivHex, uint tag)
        {
            byte[] frame = Convert.FromHexString(frameHex);
            byte[] key = Convert.FromHexString(keyHex);
            byte[] iv = Convert.FromHexString(ivHex);
            string wire;
            try
            {
                using (var encrypt = new AesCtr(key, iv))
                    wire = Convert.ToHexStringLower(encrypt.Transform(frame));

                using var stream = new MemoryStream(Convert.FromHexString(wire), writable: false);
                using var decrypt = new AesCtr(key, iv);
                (byte[] body, bool quickAck) = PacketCodec
                    .ReadPacketAsync(stream, decrypt, tag, CancellationToken.None)
                    .GetAwaiter().GetResult();
                return new(wire, Convert.ToHexStringLower(body), quickAck, null);
            }
            catch (Exception exception)
            {
                // Re-encrypting a very short frame still yields a wire; keep it
                // so error cases replay the same bytes.
                using var encryptAgain = new AesCtr(key, iv);
                wire = Convert.ToHexStringLower(encryptAgain.Transform(frame));
                return new(wire, null, false, exception.Message);
            }
        }

        public sealed record EncodeResult(string? Wire, string? Error);

        public static EncodeResult Encode(string bodyHex, uint tag)
        {
            try
            {
                return new(
                    Convert.ToHexStringLower(PacketCodec.EncodeReply(Convert.FromHexString(bodyHex), tag)),
                    null);
            }
            catch (Exception exception)
            {
                return new(null, exception.Message);
            }
        }
    }
}

namespace TgwsOne.Engine.Transport
{
    public static class FakeTlsShims
    {
        public sealed record VerifyResult(
            bool Ok, string? ClientRandom, string? SessionId, uint? Timestamp, string? Error);

        public static VerifyResult Verify(string helloHex, string secretHex, long nowSeconds)
        {
            try
            {
                bool ok = FakeTls.TryVerifyClientHello(
                    Convert.FromHexString(helloHex),
                    Convert.FromHexString(secretHex),
                    out FakeTlsClientHello hello,
                    nowSeconds);
                return ok
                    ? new(true, Convert.ToHexStringLower(hello.ClientRandom),
                        Convert.ToHexStringLower(hello.SessionId), hello.Timestamp, null)
                    : new(false, null, null, null, null);
            }
            catch (Exception exception)
            {
                return new(false, null, null, null, exception.Message);
            }
        }

        public sealed record ParseResult(int[] CipherSuites, int[] Groups, int[] KeyLengths);

        public static ParseResult Parse(string helloHex)
        {
            ClientHelloInfo info = FakeTls.ParseClientHello(Convert.FromHexString(helloHex));
            int[] groups = [.. info.KeyShares.Keys.Order()];
            return new(
                [.. info.CipherSuites.Order()],
                groups,
                [.. groups.Select(group => info.KeyShares[group])]);
        }

        public sealed record BuildResult(string Answer);

        // appDataLength null exercises the "no profile" random range.
        public static BuildResult Build(
            string secretHex,
            string clientRandomHex,
            string sessionIdHex,
            int[] suites,
            int[] groups,
            int[] keyLengths,
            int? appDataLength)
        {
            var keyShares = new Dictionary<int, int>();
            for (int i = 0; i < groups.Length; i++) keyShares[groups[i]] = keyLengths[i];
            ClientHelloInfo info = new(new HashSet<int>(suites), keyShares);
            byte[] answer = FakeTls.BuildResponse(
                Convert.FromHexString(secretHex),
                Convert.FromHexString(clientRandomHex),
                Convert.FromHexString(sessionIdHex),
                info,
                appDataLength);
            return new(Convert.ToHexStringLower(answer));
        }

        public static string Alert(byte description)
            => Convert.ToHexStringLower(FakeTls.Alert(description));

        public static int? RecordLength(string headerHex)
            => FakeTls.TryReadRecordLength(Convert.FromHexString(headerHex), out int length)
                ? length
                : null;
    }
}
