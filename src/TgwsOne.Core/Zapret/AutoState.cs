using System.Globalization;
using System.Text;

namespace TgwsOne.Core.Zapret;

/// <summary>What <c>auto</c> currently does for one host: the built-in strategy it settled on and how many failures it has counted.</summary>
public sealed record AutoHost(string Host, string Strategy, int Failures);

/// <summary>
/// Reads <c>autostate.tsv</c>, the file <c>zapret2/tgws-state.lua</c> keeps up to date inside nfqws2's writable folder.
/// The file is the only truth: a missing or unreadable one simply means there is nothing to show.
/// </summary>
public static class AutoState
{
    /// <summary>The same name the Lua script writes.</summary>
    public const string FileName = "autostate.tsv";

    /// <summary>
    /// Lines of <c>host TAB strategy number TAB failures</c>; the number (1..4) becomes the ID in
    /// <see cref="BuiltInStrategies.Rotation"/>. Lines that don't fit are skipped. Sorted by host; a host listed twice keeps its first line.
    /// </summary>
    public static IReadOnlyList<AutoHost> Read(string tsvFile)
    {
        string[] lines;
        try
        {
            lines = File.ReadAllLines(tsvFile, Encoding.UTF8);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return [];
        }

        var hosts = new Dictionary<string, AutoHost>(StringComparer.Ordinal);
        foreach (string line in lines)
        {
            string[] parts = line.TrimEnd('\r').Split('\t');
            if (parts.Length != 3 || parts[0].Length == 0) continue;
            if (!int.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out int number)
                || number < 1 || number > BuiltInStrategies.Rotation.Count) continue;
            if (!int.TryParse(parts[2], NumberStyles.None, CultureInfo.InvariantCulture, out int failures)) continue;
            hosts.TryAdd(parts[0], new AutoHost(parts[0], BuiltInStrategies.Rotation[number - 1], failures));
        }
        return [.. hosts.Values.OrderBy(h => h.Host, StringComparer.Ordinal)];
    }
}
