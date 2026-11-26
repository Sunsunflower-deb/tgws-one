using System.Buffers.Binary;
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

    [RealNetworkFact]
    public async Task WebSocketPool_ReqPq_GetsResPQ()
    {
        EngineConfig config = Config(poolSize: 1, cfProxy: false, h2: false, userDomains: ["example.test"]);
        await AssertResPQAsync(config, dcIdx: 2, warmupMs: 1500);
    }

    [RealBlockedRouteFact]
    public async Task TcpFallback_ReqPq_GetsResPQ()
    {
        // .220 is the address this network reaches; the built-in fallback table names .51, which it does not.
        EngineConfig config = Config(poolSize: 0, cfProxy: false, h2: false, userDomains: ["example.test"],
            tcpFallbackHost: "149.154.167.220");
        await AssertResPQAsync(config, dcIdx: 2, warmupMs: 0);
    }

    [RealNetworkFact]
    public async Task CfProxy_ReqPq_GetsResPQ()
    {
        // The direct TCP fallback points at TEST-NET-1: only the Cloudflare route can answer.
        // This network resets TLS to the masked Cloudflare names but allows port 80; --no-secure is exactly that route.
        EngineConfig config = Config(poolSize: 0, cfProxy: true, h2: false,
            userDomains: [DomainBalancer.DefaultDomains[0]], tcpFallbackHost: "192.0.2.1", noSecure: true);
        await AssertResPQAsync(config, dcIdx: 2, warmupMs: 0);
    }

    [RealBlockedRouteFact]
    public async Task H2Media_ReqPq_GetsResPQ()
    {
        // Media (negative DC) with no WS pool: the engine must take the Cloudflare HTTP/2 route.
        EngineConfig config = Config(poolSize: 0, cfProxy: true, h2: true,
            userDomains: [DomainBalancer.DefaultDomains[0]], tcpFallbackHost: "192.0.2.1");
        await AssertResPQAsync(config, dcIdx: -2, warmupMs: 0);
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

    private static async Task AssertResPQAsync(EngineConfig config, short dcIdx, int warmupMs)
    {
        EngineSelfCheckResult result = await EngineSelfCheck.ExchangeAsync(config, dcIdx, warmupMs: warmupMs);
        Assert.True(result.Ok, result.Failure);
        AssertResPQ(result.Reply!);
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
}
