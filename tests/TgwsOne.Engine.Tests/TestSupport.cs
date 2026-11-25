using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using TgwsOne.Core;
using TgwsOne.Engine.Crypto;

namespace TgwsOne.Engine.Tests;

/// <summary>A throwaway profile: everything the engine and the host write lands under one temporary directory.</summary>
internal sealed class TempPaths : IDisposable
{
    public string Root { get; }
    public AppPaths Paths { get; }

    /// <param name="dataDir">Where the profile goes under <see cref="Root"/>; a path of the test's choosing when the
    /// default location would not do (a Unix socket address has a length limit).</param>
    public TempPaths(string dataDir = "data")
    {
        Root = Path.Combine(Path.GetTempPath(), "tgwsone-engine-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Root);
        var env = new Dictionary<string, string>
        {
            ["TGWS_DATA_DIR"] = Path.Combine(Root, dataDir),
            ["XDG_RUNTIME_DIR"] = Path.Combine(Root, "run"),
            ["LOCALAPPDATA"] = Path.Combine(Root, "local"),
        };
        Paths = AppPaths.For(OsKind.Linux, name => env.GetValueOrDefault(name), Path.Combine(Root, "app"),
            Path.Combine(Root, "home"));
    }

    public void Configure(Action<IniFile> edit)
    {
        Directory.CreateDirectory(Paths.DataDir);
        IniFile ini = IniFile.Load(Paths.Ini);
        edit(ini);
        ini.Save();
    }

    public void Dispose()
    {
        try { Directory.Delete(Root, recursive: true); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }
}

internal static class Ports
{
    public static int Free()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    public static bool Listening(int port)
    {
        try
        {
            using var client = new TcpClient();
            client.Connect(IPAddress.Loopback, port);
            return true;
        }
        catch (SocketException)
        {
            return false;
        }
    }

    public static async Task<bool> WaitForAsync(Func<bool> condition, TimeSpan timeout)
    {
        DateTime deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (condition()) return true;
            await Task.Delay(25).ConfigureAwait(false);
        }
        return condition();
    }
}

/// <summary>
/// A Telegram Desktop client's side of the obfuscated2 handshake, built the way the real client builds it: the
/// 64 random bytes name the key, the tail carries the protocol tag and DC, and the same AES-CTR stream continues
/// into the data. The relay side is the reverse key, as in upstream <c>_build_crypto_ctx</c>.
/// </summary>
internal sealed class ClientHandshake
{
    public byte[] Bytes { get; }
    public AesCtr Encrypt { get; }
    public AesCtr Decrypt { get; }
    public uint ProtoTag { get; }

    public ClientHandshake(byte[] secret, uint protoTag, short dcIdx)
    {
        ProtoTag = protoTag;
        // Like the real client and upstream's relay-init generator: bytes 0..56 go out as the random key material
        // they look like, and only the 8-byte tail is encrypted with a keystream the proxy can reproduce.
        Bytes = new byte[MtProtoConstants.HandshakeLength];
        RandomNumberGenerator.Fill(Bytes);

        byte[] prekey = Bytes.AsSpan(MtProtoConstants.SkipLength, MtProtoConstants.PrekeyLength).ToArray();
        byte[] iv = Bytes.AsSpan(MtProtoConstants.SkipLength + MtProtoConstants.PrekeyLength,
            MtProtoConstants.IvLength).ToArray();
        byte[] key = SHA256.HashData([.. prekey, .. secret]);

        var tail = new byte[8];
        BinaryPrimitivesWriteUInt32(tail.AsSpan(0, 4), protoTag);
        BinaryPrimitivesWriteInt16(tail.AsSpan(4, 2), dcIdx);
        RandomNumberGenerator.Fill(tail.AsSpan(6, 2));

        using (var handshakeCtr = new AesCtr(key, iv))
        {
            byte[] encrypted = handshakeCtr.Transform(Bytes);
            for (int i = 0; i < 8; i++)
            {
                int at = MtProtoConstants.ProtoTagPosition + i;
                Bytes[at] = (byte)(tail[i] ^ encrypted[at] ^ Bytes[at]);
            }
        }

        Encrypt = new AesCtr(key, iv);
        Encrypt.Skip(MtProtoConstants.HandshakeLength);

        byte[] reversed = [.. Bytes.AsSpan(MtProtoConstants.SkipLength,
            MtProtoConstants.PrekeyLength + MtProtoConstants.IvLength)];
        Array.Reverse(reversed);
        byte[] decKey = SHA256.HashData([.. reversed.AsSpan(0, MtProtoConstants.PrekeyLength), .. secret]);
        byte[] decIv = reversed.AsSpan(MtProtoConstants.PrekeyLength, MtProtoConstants.IvLength).ToArray();
        Decrypt = new AesCtr(decKey, decIv);
    }

    private static void BinaryPrimitivesWriteUInt32(Span<byte> span, uint value)
        => System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(span, value);

    private static void BinaryPrimitivesWriteInt16(Span<byte> span, short value)
        => System.Buffers.Binary.BinaryPrimitives.WriteInt16LittleEndian(span, value);
}

/// <summary>The Telegram side of a relay handshake: decrypt with the announced key (after the 64-byte init), encrypt with its reverse.</summary>
internal sealed class TelegramSide
{
    public uint ProtoTag { get; }
    public short DcIdx { get; }
    public AesCtr DecryptFromProxy { get; }
    public AesCtr EncryptToProxy { get; }

    public TelegramSide(byte[] relayInit)
    {
        Assert.True(Obfuscated2.TryReadRelayInit(relayInit, out uint tag, out short dc), "relay init is not readable");
        ProtoTag = tag;
        DcIdx = dc;

        byte[] rawKey = relayInit.AsSpan(MtProtoConstants.SkipLength, MtProtoConstants.PrekeyLength).ToArray();
        byte[] rawIv = relayInit.AsSpan(MtProtoConstants.SkipLength + MtProtoConstants.PrekeyLength,
            MtProtoConstants.IvLength).ToArray();
        DecryptFromProxy = new AesCtr(rawKey, rawIv);
        DecryptFromProxy.Skip(MtProtoConstants.HandshakeLength);

        byte[] reversed = relayInit.AsSpan(MtProtoConstants.SkipLength,
            MtProtoConstants.PrekeyLength + MtProtoConstants.IvLength).ToArray();
        Array.Reverse(reversed);
        EncryptToProxy = new AesCtr(reversed.AsSpan(0, MtProtoConstants.PrekeyLength),
            reversed.AsSpan(MtProtoConstants.PrekeyLength, MtProtoConstants.IvLength));
    }
}
