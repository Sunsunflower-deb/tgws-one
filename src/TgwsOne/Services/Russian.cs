namespace TgwsOne.Services;

internal static class Russian
{
    /// <summary>The form of a noun after a number: 1 сбой, 2 сбоя, 5 сбоев; 11 to 14 are always the "many" form.</summary>
    public static string Plural(int n, string one, string few, string many)
    {
        int lastTwo = Math.Abs(n) % 100;
        if (lastTwo is >= 11 and <= 14) return many;
        return (lastTwo % 10) switch
        {
            1 => one,
            >= 2 and <= 4 => few,
            _ => many,
        };
    }
}
