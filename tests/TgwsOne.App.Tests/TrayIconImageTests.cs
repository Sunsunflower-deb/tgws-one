using Avalonia.Headless.XUnit;
using Avalonia.Platform;
using SkiaSharp;
using TgwsOne.Services;

namespace TgwsOne.Tests;

/// <summary>The tray icon is the app icon plus a dot; the pixels are checked, not just that something was produced.</summary>
public class TrayIconImageTests
{
    private static Stream AppIcon() => AssetLoader.Open(new Uri("avares://TgwsOne/Assets/app.ico"));

    private static SKBitmap Render(Tone tone, int size = TrayIconImage.DefaultSize)
    {
        using Stream icon = AppIcon();
        return SKBitmap.Decode(TrayIconImage.RenderPng(icon, tone, size))!;
    }

    private static SKColor CenterOfDot(SKBitmap bitmap)
    {
        (float x, float y, _) = TrayIconImage.Dot(bitmap.Width);
        return bitmap.GetPixel((int)x, (int)y);
    }

    [AvaloniaTheory]
    [InlineData("Green")]
    [InlineData("Amber")]
    [InlineData("Grey")]
    [InlineData("Red")]
    public void TheDot_IsInTheColourOfItsTone(string toneName)
    {
        var tone = Enum.Parse<Tone>(toneName);
        using SKBitmap bitmap = Render(tone);

        Assert.Equal(TrayIconImage.ColorOf(tone), CenterOfDot(bitmap));
    }

    [AvaloniaFact]
    public void TheFourTones_AreFourDifferentColours()
    {
        var colours = new[] { Tone.Green, Tone.Amber, Tone.Grey, Tone.Red }.Select(t => TrayIconImage.ColorOf(t)).ToList();

        Assert.Equal(4, colours.Distinct().Count());
    }

    [AvaloniaFact]
    public void TheIconBehindTheDot_IsTheAppIcon_AndIsTheSameInEveryTone()
    {
        using SKBitmap green = Render(Tone.Green);
        using SKBitmap red = Render(Tone.Red);
        (float cx, float cy, float radius) = TrayIconImage.Dot(green.Width);

        int visible = 0;
        for (int y = 0; y < green.Height; y++)
        {
            for (int x = 0; x < green.Width; x++)
            {
                bool inDot = Math.Sqrt(Math.Pow(x + 0.5 - cx, 2) + Math.Pow(y + 0.5 - cy, 2)) <= radius + 1.5;
                if (inDot) continue;
                Assert.Equal(green.GetPixel(x, y), red.GetPixel(x, y));
                if (green.GetPixel(x, y).Alpha > 0) visible++;
            }
        }
        Assert.True(visible > 200, $"the app icon is not under the dot: only {visible} visible pixels outside it");
    }

    [AvaloniaTheory]
    [InlineData(16)]
    [InlineData(24)]
    [InlineData(32)]
    [InlineData(64)]
    public void TheIconIsDrawnAtTheSizeAsked_WithTheDotInsideIt(int size)
    {
        using SKBitmap bitmap = Render(Tone.Green, size);

        Assert.Equal(size, bitmap.Width);
        Assert.Equal(size, bitmap.Height);
        (float x, float y, float radius) = TrayIconImage.Dot(size);
        Assert.True(x + radius <= size && y + radius <= size);
        Assert.Equal(TrayIconImage.ColorOf(Tone.Green), CenterOfDot(bitmap));
    }

    [Fact]
    public void AnIconThatCannotBeDecoded_StillGivesTheDot()
    {
        using var garbage = new MemoryStream([1, 2, 3, 4, 5]);

        using SKBitmap bitmap = SKBitmap.Decode(TrayIconImage.RenderPng(garbage, Tone.Amber))!;

        Assert.Equal(TrayIconImage.ColorOf(Tone.Amber), CenterOfDot(bitmap));
        Assert.Equal(0, bitmap.GetPixel(2, 2).Alpha);   // nothing else is there
    }
}
