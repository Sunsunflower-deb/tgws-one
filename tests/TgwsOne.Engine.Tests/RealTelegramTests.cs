using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using TgwsOne.Engine.Crypto;
using TgwsOne.Engine.Relay;
using TgwsOne.Engine.Transport;

namespace TgwsOne.Engine.Tests;

/// <summary>Opt-in: needs the real Telegram data centres (set TGWS_REAL_NETWORK=1).</summary>
public sealed class RealNetworkFactAttribute : FactAttribute
{
    public const string Variable = "TGWS_REAL_NETWORK";

    public RealNetworkFactAttribute()
    {
        if (Environment.GetEnvironmentVariable(Variable) != "1")
            Skip = $"opt-in: set {Variable}=1 to talk to Telegram";
    }
}

/// <summary>
/// A route many networks block independently of Telegram: the raw DC addresses on 443, and TLS to the masked
/// Cloudflare names (which is what the HTTP/2 media route needs). Opt-in separately.
/// </summary>
public sealed class RealBlockedRouteFactAttribute : FactAttribute
{
    public const string Variable = "TGWS_REAL_BLOCKED";

    public RealBlockedRouteFactAttribute()
    {
        if (Environment.GetEnvironmentVariable(RealNetworkFactAttribute.Variable) != "1")
            Skip = $"opt-in: set {RealNetworkFactAttribute.Variable}=1 first";
        else if (Environment.GetEnvironmentVariable(Variable) != "1")
            Skip = $"opt-in: set {Variable}=1 (raw DC TCP / TLS-to-Cloudflare routes)";
    }
}

/// <summary>
/// End to end against Telegram itself: a real client handshake, a real unencrypted <c>req_pq_multi</c>, and the
/// <c>resPQ</c> Telegram sends back — decrypted with the client's side of the obfuscated2 streams. Each test picks
/// one route (direct WebSocket pool, CF proxy, HTTP/2 media, plain TCP fallback) and proves it carries MTProto.
/// </summary>
public class RealTelegramTests
{
    private const string SecretHex = "0123456789abcdef0123456789abcdef";
    private static readonly byte[] Secret = Convert.FromHexString(SecretHex);

    [RealNetworkFact]
    public async Task WebSocketPool_ReqPq_GetsResPQ()
    {
        EngineConfig config = Config(poolSize: 1, cfProxy: false, h2: false, userDomains: ["example.test"]);
        byte[] reply = await ExchangeAsync(config, dc: 2, dcIdx: 2, warmupMs: 1500);
        AssertResPQ(reply);
    }

    [RealBlockedRouteFact]
    public async Task TcpFallback_ReqPq_GetsResPQ()
    {
        // .220 is the address this network reaches; the built-in fallback table names .51, which it does not.
        EngineConfig config = Config(poolSize: 0, cfProxy: false, h2: false, userDomains: ["example.test"],
            tcpFallbackHost: "149.154.167.220");
        byte[] reply = await ExchangeAsync(config, dc: 2, dcIdx: 2, warmupMs: 0);
        AssertResPQ(reply);
    }

    [RealNetworkFact]
    public async Task CfProxy_ReqPq_GetsResPQ()
    {
        // The direct TCP fallback points at TEST-NET-1: only the Cloudflare route can answer.
        // This network resets TLS to the masked Cloudflare names but allows port 80; --no-secure is exactly that route.
        EngineConfig config = Config(poolSize: 0, cfProxy: true, h2: false,
            userDomains: [DomainBalancer.DefaultDomains[0]], tcpFallbackHost: "192.0.2.1", noSecure: true);
        byte[] reply = await ExchangeAsync(config, dc: 2, dcIdx: 2, warmupMs: 0);
        AssertResPQ(reply);
    }

    [RealBlockedRouteFact]
    public async Task H2Media_ReqPq_GetsResPQ()
    {
        // Media (negative DC) with no WS pool: the engine must take the Cloudflare HTTP/2 route.
        EngineConfig config = Config(poolSize: 0, cfProxy: true, h2: true,
            userDomains: [DomainBalancer.DefaultDomains[0]], tcpFallbackHost: "192.0.2.1");
        byte[] reply = await ExchangeAsync(config, dc: 2, dcIdx: -2, warmupMs: 0);
        AssertResPQ(reply);
    }

    private static EngineConfig Config(int poolSize, bool cfProxy, bool h2, string[] userDomains,
        string? tcpFallbackHost = null, bool noSecure = false)
        => new()
        {
            Host = "127.0.0.1",
            Port = Ports.Free(),
            Secret = SecretHex,
            DcRedirects = poolSize > 0
                ? new Dictionary<int, string> { [2] = "149.154.167.220" }
                : new Dictionary<int, string>(),
            PoolSize = poolSize,
            FallbackCfProxy = cfProxy,
            CfproxyH2Media = h2,
            CfproxyUserDomains = userDomains,
            DisableSecure = noSecure,
            LogFile = null,
            TcpFallbackHostOverride = tcpFallbackHost,
        };

    private static async Task<byte[]> ExchangeAsync(EngineConfig config, int dc, short dcIdx, int warmupMs)
    {
        await using var engine = new ProxyEngine(config);
        await engine.StartAsync(CancellationToken.None);
        try
        {
            if (warmupMs > 0) await Task.Delay(warmupMs);

            var client = new ClientHandshake(Secret, MtProtoConstants.ProtoIntermediate, dcIdx);
            using var tcp = new TcpClient();
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            await tcp.ConnectAsync(IPAddress.Loopback, config.Port, timeout.Token);
            NetworkStream stream = tcp.GetStream();

            await stream.WriteAsync(client.Bytes, timeout.Token);
            await stream.WriteAsync(client.Encrypt.Transform(ReqPqMulti()), timeout.Token);
            await stream.FlushAsync(timeout.Token);

            byte[] lengthBytes = await ReadEncryptedAsync(stream, 4, client.Decrypt, timeout.Token);
            int length = BinaryPrimitives.ReadInt32LittleEndian(lengthBytes);
            Assert.InRange(length, 40, 1024 * 1024);
            return await ReadEncryptedAsync(stream, length, client.Decrypt, timeout.Token);
        }
        finally
        {
            await engine.StopAsync();
        }
    }

    private static void AssertResPQ(byte[] reply)
    {
        // auth_key_id = 0 for an unencrypted MTProto message; then message_id (8), length (4), body.
        Assert.Equal(new byte[8], reply.AsSpan(0, 8).ToArray());
        int bodyLength = BinaryPrimitives.ReadInt32LittleEndian(reply.AsSpan(16, 4));
        Assert.Equal(reply.Length - 20, bodyLength);
        uint constructor = BinaryPrimitives.ReadUInt32LittleEndian(reply.AsSpan(20, 4));
        Assert.Equal(0x05162463u, constructor);   // resPQ
        Assert.True(reply.Length > 40, "resPQ carries a nonce and a server nonce");
    }

    /// <summary>An unencrypted MTProto <c>req_pq_multi</c>, wrapped in the intermediate transport's length prefix.</summary>
    private static byte[] ReqPqMulti()
    {
        var body = new byte[20];
        BinaryPrimitives.WriteUInt32LittleEndian(body, 0xBE7E8EF1u);   // req_pq_multi
        RandomNumberGenerator.Fill(body.AsSpan(4, 16));                // nonce

        long seconds = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        long messageId = (seconds << 32) | (uint)(Random.Shared.Next(1 << 20) << 2);
        var message = new byte[20 + body.Length];
        BinaryPrimitives.WriteInt64LittleEndian(message.AsSpan(8), messageId);
        BinaryPrimitives.WriteInt32LittleEndian(message.AsSpan(16, 4), body.Length);
        body.CopyTo(message, 20);

        var packet = new byte[4 + message.Length];
        BinaryPrimitives.WriteInt32LittleEndian(packet, message.Length);
        message.CopyTo(packet, 4);
        return packet;
    }

    private static async Task<byte[]> ReadEncryptedAsync(
        Stream stream, int count, AesCtr decrypt, CancellationToken ct)
    {
        var encrypted = new byte[count];
        await stream.ReadExactlyAsync(encrypted, ct);
        return decrypt.Transform(encrypted);
    }
}
