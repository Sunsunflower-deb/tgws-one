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

    // Status marks (the banner, check lists)
    public const string Info = "M12 22a10 10 0 1 0 0-20 10 10 0 0 0 0 20z M12 16v-4 M12 8h.01";
    public const string Warning = "M10.29 3.86L1.82 18a2 2 0 0 0 1.71 3h16.94a2 2 0 0 0 1.71-3L13.71 3.86a2 2 0 0 0-3.42 0z M12 9v4 M12 17h.01";
    public const string Error = "M12 22a10 10 0 1 0 0-20 10 10 0 0 0 0 20z M15 9l-6 6 M9 9l6 6";
    public const string Check = "M22 11.08V12a10 10 0 1 1-5.93-9.14 M22 4L12 14.01l-3-3";

    // Controls
    public const string Chevron = "M9 18l6-6-6-6";
    public const string More = "M5 12h.01 M12 12h.01 M19 12h.01";
    public const string Close = "M18 6L6 18 M6 6l12 12";
    public const string Copy = "M20 9h-9a2 2 0 0 0-2 2v9a2 2 0 0 0 2 2h9a2 2 0 0 0 2-2v-9a2 2 0 0 0-2-2z M5 15H4a2 2 0 0 1-2-2V4a2 2 0 0 1 2-2h9a2 2 0 0 1 2 2v1";
    public const string Send = "M22 2L11 13 M22 2l-7 20-4-9-9-4 20-7z";
    public const string Key = "M21 2l-2 2m-7.61 7.61a5.5 5.5 0 1 1-7.78 7.78 5.5 5.5 0 0 1 7.78-7.78zm0 0L15.5 7.5m0 0l3 3L22 7l-3-3m-3.5 3.5L19 4";
}
