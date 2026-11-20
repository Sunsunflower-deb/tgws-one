using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Media;
using TgwsOne.Services;

namespace TgwsOne.Views;

/// <summary>
/// An outline icon from <see cref="IconPaths"/>: the path data (24×24 grid) is scaled to the control's size and stroked with
/// the inherited foreground, so it follows the selection and theme colours of whatever it sits in.
/// </summary>
internal sealed class Icon : Control
{
    private const double Grid = 24;

    public static readonly StyledProperty<string?> DataProperty = AvaloniaProperty.Register<Icon, string?>(nameof(Data));
    public static readonly StyledProperty<double> StrokeWidthProperty = AvaloniaProperty.Register<Icon, double>(nameof(StrokeWidth), 2.0);
    public static readonly StyledProperty<IBrush?> ForegroundProperty = TextElement.ForegroundProperty.AddOwner<Icon>();
    public static readonly StyledProperty<Tone?> ToneProperty = AvaloniaProperty.Register<Icon, Tone?>(nameof(Tone));

    private Geometry? _geometry;

    static Icon()
    {
        AffectsRender<Icon>(DataProperty, StrokeWidthProperty, ForegroundProperty, ToneProperty);
    }

    public Icon()
    {
        ActualThemeVariantChanged += (_, _) => InvalidateVisual();   // a tone's brush differs per theme
    }

    /// <summary>When set, the icon is drawn in this status colour instead of the inherited foreground.</summary>
    public Tone? Tone
    {
        get => GetValue(ToneProperty);
        set => SetValue(ToneProperty, value);
    }

    /// <summary>SVG path data on a 24×24 grid.</summary>
    public string? Data
    {
        get => GetValue(DataProperty);
        set => SetValue(DataProperty, value);
    }

    /// <summary>In grid units (2 on the 24 grid is 1.5 px on an 18 px icon).</summary>
    public double StrokeWidth
    {
        get => GetValue(StrokeWidthProperty);
        set => SetValue(StrokeWidthProperty, value);
    }

    public IBrush? Foreground
    {
        get => GetValue(ForegroundProperty);
        set => SetValue(ForegroundProperty, value);
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == DataProperty) _geometry = null;
    }

    public override void Render(DrawingContext context)
    {
        IBrush? brush = Tone is { } tone ? ToneBrushes.Find(this, tone) : Foreground;
        if (string.IsNullOrEmpty(Data) || brush is null) return;
        _geometry ??= Geometry.Parse(Data);

        double scale = Math.Min(Bounds.Width, Bounds.Height) / Grid;
        var pen = new Pen(brush, StrokeWidth, lineCap: PenLineCap.Round, lineJoin: PenLineJoin.Round);
        using (context.PushTransform(Matrix.CreateScale(scale, scale)))
            context.DrawGeometry(null, pen, _geometry);
    }
}
