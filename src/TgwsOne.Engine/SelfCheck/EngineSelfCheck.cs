using System.Buffers.Binary;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using TgwsOne.Engine.Crypto;
using TgwsOne.Engine.Relay;

namespace TgwsOne.Engine;

/// <summary>
/// The outcome of one self-check exchange: whether the engine answered, how long it took, the decrypted reply, or
/// what went wrong. A reply is only marked successful when it is a complete MTProto message the client could read.
/// </summary>
public sealed record EngineSelfCheckResult(bool Ok, TimeSpan Elapsed, byte[]? Reply, string? Failure);

/// <summary>
/// A client-side probe of a running engine: it performs the obfuscated2 handshake, sends an unencrypted
/// <c>req_pq_multi</c> and reads the <c>resPQ</c> back, exactly as Telegram Desktop would. This is what the mobile
/// application and the real-network tests report as "the engine works"; it lives in the engine rather than in the
/// tests so both use the same exchange.
/// </summary>
public static class EngineSelfCheck
{
    private static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(20);

    /// <param name="clientSecretHex">The secret the client presents; by default the engine's own, which succeeds. A
    /// different one proves that a wrong secret is refused.</param>
    /// <param name="warmupMs">Time the engine gets to fill its pools before the client connects.</param>
    /// <param name="timeout">How long the whole exchange may take; the caller's token can stop it sooner.</param>
    public static async Task<EngineSelfCheckResult> ExchangeAsync(
        EngineConfig config,
        short dcIdx,
        string? clientSecretHex = null,
        int warmupMs = 0,
        TimeSpan? timeout = null,
        CancellationToken ct = default)
    {
        long started = Stopwatch.GetTimestamp();
        TimeSpan Elapsed() => Stopwatch.GetElapsedTime(started);

        string secretHex = clientSecretHex ?? config.Secret;
        if (!EngineConfig.IsValidSecret(secretHex))
            return new(false, Elapsed(), null, "the client secret is not 32 hexadecimal digits");
        byte[] secret = Convert.FromHexString(secretHex);

        await using var engine = new ProxyEngine(config);
        try
        {
            await engine.StartAsync(ct).ConfigureAwait(false);
            if (warmupMs > 0) await Task.Delay(warmupMs, ct).ConfigureAwait(false);

            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
            deadline.CancelAfter(timeout ?? DefaultTimeout);

            var client = new ClientHandshake(secret, MtProtoConstants.ProtoIntermediate, dcIdx);
            using var tcp = new TcpClient();
            await tcp.ConnectAsync(IPAddress.Loopback, config.Port, deadline.Token).ConfigureAwait(false);
            NetworkStream stream = tcp.GetStream();

            await stream.WriteAsync(client.Bytes, deadline.Token).ConfigureAwait(false);
            await stream.WriteAsync(client.Encrypt.Transform(ReqPqMulti()), deadline.Token).ConfigureAwait(false);
            await stream.FlushAsync(deadline.Token).ConfigureAwait(false);

            byte[] lengthBytes = await ReadDecryptedAsync(stream, 4, client.Decrypt, deadline.Token)
                .ConfigureAwait(false);
            int length = BinaryPrimitives.ReadInt32LittleEndian(lengthBytes);
            if (length is < 40 or > 1024 * 1024)
                return new(false, Elapsed(), null, $"the engine sent an unexpected reply length {length}");

            byte[] reply = await ReadDecryptedAsync(stream, length, client.Decrypt, deadline.Token)
                .ConfigureAwait(false);
            return new(true, Elapsed(), reply, null);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            return new(false, Elapsed(), null, ex.Message);
        }
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

    private static async Task<byte[]> ReadDecryptedAsync(
        Stream stream, int count, AesCtr decrypt, CancellationToken ct)
    {
        var encrypted = new byte[count];
        await stream.ReadExactlyAsync(encrypted, ct).ConfigureAwait(false);
        return decrypt.Transform(encrypted);
    }
}
