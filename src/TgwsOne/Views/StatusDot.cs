using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using TgwsOne.Services;

namespace TgwsOne.Views;

/// <summary>A filled circle in the colour of a <see cref="Tone"/>. Drawn, not typed: there is no glyph to go missing from a font.</summary>
internal sealed class StatusDot : Control
{
    public static readonly StyledProperty<Tone> ToneProperty = AvaloniaProperty.Register<StatusDot, Tone>(nameof(Tone), Tone.Grey);

    static StatusDot()
    {
        AffectsRender<StatusDot>(ToneProperty);
    }

    public StatusDot()
    {
        ActualThemeVariantChanged += (_, _) => InvalidateVisual();   // the tone brushes differ per theme
    }

    public Tone Tone
    {
        get => GetValue(ToneProperty);
        set => SetValue(ToneProperty, value);
    }

    public override void Render(DrawingContext context)
    {
        if (ToneBrushes.Find(this, Tone) is not { } brush) return;
        double diameter = Math.Min(Bounds.Width, Bounds.Height);
        context.DrawEllipse(brush, null, new Point(Bounds.Width / 2, Bounds.Height / 2), diameter / 2, diameter / 2);
    }
}
