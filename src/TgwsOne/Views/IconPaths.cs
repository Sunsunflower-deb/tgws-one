namespace TgwsOne.Views;

/// <summary>
/// Every icon of the app: SVG path data on a 24×24 grid, drawn as an outline by <see cref="Icon"/>. Shapes, not glyphs —
/// the machine this was built on has no emoji font, and a Windows box without Segoe UI Symbol would show squares too.
/// The outlines are in the style of the Feather set (MIT).
/// </summary>
internal static class IconPaths
{
    public const string Home = "M3 9.5L12 3l9 6.5V20a1 1 0 0 1-1 1H4a1 1 0 0 1-1-1z M9 21v-7h6v7";
    public const string Shield = "M12 22s8-4 8-10V5l-8-3-8 3v7c0 6 8 10 8 10z";
    public const string Activity = "M22 12h-4l-3 9L9 3l-3 9H2";
    public const string List = "M8 6h13 M8 12h13 M8 18h13 M3 6h.01 M3 12h.01 M3 18h.01";
    public const string Sliders = "M4 21v-7 M4 10V3 M12 21v-9 M12 8V3 M20 21v-5 M20 12V3 M1 14h6 M9 8h6 M17 16h6";
}
