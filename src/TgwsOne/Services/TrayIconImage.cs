using SkiaSharp;

namespace TgwsOne.Services;

/// <summary>
/// The tray icon: the application's icon with a status dot in the corner, in the colour of the headline (green, amber, grey, red).
/// Drawn into a PNG, one per <see cref="Tone"/>: a tray has no way to tint an icon, and a dot is what tells the state at a glance.
/// </summary>
internal static class TrayIconImage
{
    public const int DefaultSize = 32;

    // The theme's own colours (the light variants: a white ring round the dot keeps it readable on a dark panel as well).
    public static SKColor ColorOf(Tone tone) => tone switch
    {
        Tone.Green => new SKColor(0x1E, 0x8E, 0x3E),
        Tone.Amber => new SKColor(0xC7, 0x7C, 0x02),
        Tone.Red => new SKColor(0xD9, 0x30, 0x25),
        _ => new SKColor(0x8A, 0x93, 0xA0),
    };

    /// <summary>The dot's centre and the radius of its ring, for an icon of <paramref name="size"/> pixels.</summary>
    public static (float X, float Y, float Radius) Dot(int size)
    {
        float radius = size * 0.26f;
        float edge = size - radius - size * 0.02f;
        return (edge, edge, radius);
    }

    /// <param name="baseIcon">The application icon (the .ico; its largest picture is used, scaled to <paramref name="size"/>). When it cannot be decoded the dot is drawn alone.</param>
    public static byte[] RenderPng(Stream baseIcon, Tone tone, int size = DefaultSize)
    {
        using SKBitmap? source = Decode(baseIcon);
        using var surface = SKSurface.Create(new SKImageInfo(size, size, SKColorType.Rgba8888, SKAlphaType.Premul));
        SKCanvas canvas = surface.Canvas;
        canvas.Clear(SKColors.Transparent);

        if (source != null)
        {
            using var image = SKImage.FromBitmap(source);
            canvas.DrawImage(image, new SKRect(0, 0, size, size), new SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.Linear));
        }

        (float x, float y, float radius) = Dot(size);
        using var ring = new SKPaint { IsAntialias = true, Color = new SKColor(255, 255, 255, 235) };
        using var fill = new SKPaint { IsAntialias = true, Color = ColorOf(tone) };
        canvas.DrawCircle(x, y, radius, ring);
        canvas.DrawCircle(x, y, radius * 0.76f, fill);

        using SKImage result = surface.Snapshot();
        using SKData png = result.Encode(SKEncodedImageFormat.Png, 100);
        return png.ToArray();
    }

    private static SKBitmap? Decode(Stream stream)
    {
        try { return SKBitmap.Decode(stream); }
        catch (Exception ex) when (ex is IOException or ArgumentException) { return null; }
    }
}
