using Avalonia.Data.Converters;
using Avalonia.Media;
using TgwsOne.Services;

namespace TgwsOne.Views;

/// <summary>Small bool-to-look converters for the finder's table.</summary>
internal static class FinderConverters
{
    public static readonly FuncValueConverter<bool, FontWeight> BoldIfBest = new(best => best ? FontWeight.SemiBold : FontWeight.Normal);

    /// <summary>The Cloudflare column: a tick when the whole download arrived, a cross when it did not.</summary>
    public static readonly FuncValueConverter<bool, string?> CheckOrCross = new(ok => ok ? IconPaths.TickMark : IconPaths.CrossMark);

    public static readonly FuncValueConverter<bool, Tone?> GreenOrRed = new(ok => ok ? Tone.Green : Tone.Red);
}
