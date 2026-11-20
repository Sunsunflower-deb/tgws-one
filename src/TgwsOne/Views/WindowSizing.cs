namespace TgwsOne.Views;

/// <summary>
/// The size the main window opens at: as big as the pages want (the "Обход DPI" page needs about 730 px of height), but never
/// bigger than the screen it opens on can show.
/// </summary>
internal static class WindowSizing
{
    public const double DefaultWidth = 880;
    public const double DefaultHeight = 730;
    public const double MinWidth = 720;
    public const double MinHeight = 480;

    /// <summary>
    /// What the working area keeps back for the window's own title bar and frame, which the window's size does not count.
    /// </summary>
    public const double Margin = 40;

    /// <summary>
    /// The size for a screen whose working area (the screen without the taskbar or panel) is
    /// <paramref name="workWidth"/> × <paramref name="workHeight"/> device-independent pixels. Never below the window's minimum:
    /// on a screen too small for that the window can't be made to fit anyway.
    /// </summary>
    public static (double Width, double Height) Fit(double workWidth, double workHeight)
        => (Clamp(DefaultWidth, MinWidth, workWidth), Clamp(DefaultHeight, MinHeight, workHeight));

    private static double Clamp(double wanted, double min, double work)
        => Math.Max(min, Math.Min(wanted, work - Margin));
}
