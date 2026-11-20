using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Styling;
using TgwsOne.Services;

namespace TgwsOne.Views;

/// <summary>Finds the brush of a <see cref="Tone"/> in the resources of the theme the element is in right now.</summary>
internal static class ToneBrushes
{
    public static string Key(Tone tone) => tone switch
    {
        Tone.Green => "ToneGreen",
        Tone.Amber => "ToneAmber",
        Tone.Red => "ToneRed",
        _ => "ToneGrey",
    };

    public static IBrush? Find(StyledElement element, Tone tone)
        => element.TryFindResource(Key(tone), element.ActualThemeVariant, out object? brush) ? brush as IBrush : null;
}
