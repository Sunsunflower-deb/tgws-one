using System.Net.Sockets;
using TgwsOne.Engine.Crypto;
using TgwsOne.Engine.MtProto;
using TgwsOne.Engine.Transport;

namespace TgwsOne.Engine.Relay;

/// <summary>
/// The bidirectional relay with re-encryption: client ciphertext is decrypted with the key from its handshake,
/// re-encrypted with the Telegram side's key, and sent on; replies travel back the other way. The two directions
/// run as two tasks and the first to finish ends the session — a half-open relay would hold sockets for nothing.
/// </summary>
internal static class RelayBridge
{
    private const int ReadSize = 65536;

    public static async Task WebSocketAsync(
        Stream client, RawWebSocket ws, CryptoContext ctx, MsgSplitter? splitter,
        string label, string dcTag, EngineStats stats, EngineLog log, CancellationToken ct)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct);
        long upBytes = 0, downBytes = 0, upPackets = 0, downPackets = 0;
        string closeReason = "normal";
        var started = System.Diagnostics.Stopwatch.StartNew();

        async Task ClientToTelegram()
        {
            var readBuffer = new byte[ReadSize];
            try
            {
                while (!linked.IsCancellationRequested)
                {
                    int read = await client.ReadAsync(readBuffer, linked.Token).ConfigureAwait(false);
                    if (read == 0)
                    {
                        if (splitter is not null)
                        {
                            foreach (byte[] tail in splitter.Flush())
                                await ws.SendAsync(tail, linked.Token).ConfigureAwait(false);
                        }
                        break;
                    }

                    stats.Up(read);
                    upBytes += read;
                    upPackets++;

                    var plain = new byte[read];
                    ctx.CltDec.Transform(readBuffer.AsSpan(0, read), plain);
                    var cipher = new byte[read];
                    ctx.TgEnc.Transform(plain, cipher);

                    if (splitter is null)
                    {
                        await ws.SendAsync(cipher, linked.Token).ConfigureAwait(false);
                    }
                    else
                    {
                        IReadOnlyList<byte[]> parts = splitter.Split(cipher, plain);
                        if (parts.Count == 0) continue;
                        if (parts.Count == 1) await ws.SendAsync(parts[0], linked.Token).ConfigureAwait(false);
                        else await ws.SendBatchAsync(parts, linked.Token).ConfigureAwait(false);
                    }
                }
            }
            catch (OperationCanceledException) { }
            catch (Exception ex) when (ex is IOException or SocketException or ObjectDisposedException or EndOfStreamException)
            {
                closeReason = $"client: {ex.GetType().Name}";
            }
            catch (Exception ex)
            {
                closeReason = $"client: {ex.GetType().Name}: {ex.Message}";
                log.Debug($"[{label}] tcp->ws ended: {ex.Message}");
            }
            finally
            {
                await linked.CancelAsync().ConfigureAwait(false);
            }
        }

        async Task TelegramToClient()
        {
            try
            {
                while (!linked.IsCancellationRequested)
                {
                    byte[]? data = await ws.ReceiveAsync(linked.Token).ConfigureAwait(false);
                    if (data is null)
                    {
                        if (closeReason == "normal") closeReason = "upstream: ws_close";
                        break;
                    }

                    stats.Down(data.Length);
                    downBytes += data.Length;
                    downPackets++;

                    var plain = new byte[data.Length];
                    ctx.TgDec.Transform(data, plain);
                    var cipher = new byte[data.Length];
                    ctx.CltEnc.Transform(plain, cipher);
                    await client.WriteAsync(cipher, linked.Token).ConfigureAwait(false);
                    await client.FlushAsync(linked.Token).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException) { }
            catch (Exception ex) when (ex is IOException or SocketException or ObjectDisposedException or EndOfStreamException)
            {
                closeReason = $"upstream: {ex.GetType().Name}";
            }
            catch (Exception ex)
            {
                closeReason = $"upstream: {ex.GetType().Name}: {ex.Message}";
                log.Debug($"[{label}] ws->tcp ended: {ex.Message}");
            }
            finally
            {
                await linked.CancelAsync().ConfigureAwait(false);
            }
        }

        Task up = ClientToTelegram();
        Task down = TelegramToClient();
        await Task.WhenAny(up, down).ConfigureAwait(false);
        await linked.CancelAsync().ConfigureAwait(false);
        await Task.WhenAll(up, down).ConfigureAwait(false);

        log.Info($"[{label}] {dcTag} WS session closed ({closeReason}): " +
                 $"^{EngineStats.HumanBytes(upBytes)} ({upPackets} pkts) " +
                 $"v{EngineStats.HumanBytes(downBytes)} ({downPackets} pkts) in {started.Elapsed.TotalSeconds:0.0}s");

        try { await ws.CloseAsync().ConfigureAwait(false); }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException) { }
        try
        {
            client.Close();
        }
        catch (IOException) { }
    }

    public static async Task TcpAsync(
        Stream client, TcpClient remote, CryptoContext ctx, string label,
        EngineStats stats, EngineLog log, CancellationToken ct)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct);
        NetworkStream remoteStream = remote.GetStream();

        async Task Forward(Stream source, Stream destination, bool isUp)
        {
            var buffer = new byte[ReadSize];
            try
            {
                while (!linked.IsCancellationRequested)
                {
                    int read = await source.ReadAsync(buffer, linked.Token).ConfigureAwait(false);
                    if (read == 0) break;
                    if (isUp) stats.Up(read); else stats.Down(read);

                    var plain = new byte[read];
                    var cipher = new byte[read];
                    if (isUp)
                    {
                        ctx.CltDec.Transform(buffer.AsSpan(0, read), plain);
                        ctx.TgEnc.Transform(plain, cipher);
                    }
                    else
                    {
                        ctx.TgDec.Transform(buffer.AsSpan(0, read), plain);
                        ctx.CltEnc.Transform(plain, cipher);
                    }
                    await destination.WriteAsync(cipher, linked.Token).ConfigureAwait(false);
                    await destination.FlushAsync(linked.Token).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException) { }
            catch (Exception ex) when (ex is IOException or SocketException or ObjectDisposedException)
            {
                log.Debug($"[{label}] tcp forward ended: {ex.Message}");
            }
            finally
            {
                await linked.CancelAsync().ConfigureAwait(false);
            }
        }

        Task up = Forward(client, remoteStream, isUp: true);
        Task down = Forward(remoteStream, client, isUp: false);
        await Task.WhenAny(up, down).ConfigureAwait(false);
        await linked.CancelAsync().ConfigureAwait(false);
        await Task.WhenAll(up, down).ConfigureAwait(false);

        try { remote.Dispose(); } catch (Exception ex) when (ex is IOException or SocketException) { }
        try { client.Close(); } catch (IOException) { }
    }
}
