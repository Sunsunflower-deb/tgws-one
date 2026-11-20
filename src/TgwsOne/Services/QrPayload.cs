using System.Net;
using QRCoder;

namespace TgwsOne.Services;

/// <summary>What the QR code for a phone says, and the picture of it.</summary>
internal static class QrPayload
{
    /// <summary>
    /// The link Telegram on a phone opens from a camera scan: <c>https://t.me/proxy</c>, because the camera app only follows web
    /// links (a <c>tg://</c> link would be shown as text). <paramref name="secret"/> is the 32 hex digits, which the link carries
    /// after <c>dd</c> (the mode of the proxy that hides itself as ordinary TLS).
    /// </summary>
    public static string Build(IPAddress lanAddress, int port, string secret)
        => $"https://t.me/proxy?server={Uri.EscapeDataString(lanAddress.ToString())}&port={port}&secret=dd{secret}";

    /// <summary>
    /// A PNG of the code: dark modules on white with the quiet zone around them, whatever the theme of the window it is shown in
    /// (a light code on a dark ground is one many phones will not read).
    /// </summary>
    public static byte[] Png(string payload, int pixelsPerModule = 8)
    {
        using var generator = new QRCodeGenerator();
        using QRCodeData data = generator.CreateQrCode(payload, QRCodeGenerator.ECCLevel.M);
        return new PngByteQRCode(data).GetGraphic(pixelsPerModule);
    }
}
