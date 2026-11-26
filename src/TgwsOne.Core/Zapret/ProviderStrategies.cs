using System.Text.RegularExpressions;

namespace TgwsOne.Core.Zapret;

/// <summary>
/// Which built-in strategies the <c>auto</c> rotation walks, per operator or relay.
/// <para>
/// The set is administrator data: <c>&lt;provider&gt;.rotation</c> in the service's strategies folder, one strategy ID
/// per line, <c>#</c> starting a comment — the same place and the same ownership as hand-written custom strategies.
/// The universal set is used whenever the provider is unknown, its file is unreadable, or it names no usable strategy:
/// a mistyped provider must never leave the machine without a working bypass.
/// </para>
/// </summary>
public static partial class ProviderStrategies
{
    /// <summary>The default provider: the universal rotation every other name falls back to.</summary>
    public const string Universal = "universal";

    public const string Extension = ".rotation";

    [GeneratedRegex(@"^[a-z0-9][a-z0-9._-]{0,31}\z", RegexOptions.CultureInvariant)]
    private static partial Regex ProviderRegex();

    /// <summary>Lower-case letters, digits, <c>.</c>, <c>_</c>, <c>-</c>; at most 32 characters.</summary>
    public static bool IsValidProvider(string? name)
        => name is not null && ProviderRegex().IsMatch(name);

    /// <summary>
    /// The rotation for <paramref name="provider"/>: its own file when it has a usable one, the universal set otherwise.
    /// </summary>
    public static IReadOnlyList<string> Rotation(string strategiesDir, string? provider)
    {
        if (provider is null || !IsValidProvider(provider) || provider == Universal)
            return BuiltInStrategies.Rotation;
        return Expand(ReadLines(Path.Combine(strategiesDir, provider + Extension)));
    }

    /// <summary>
    /// Keeps the built-in strategy IDs in file order. <c>auto</c> is dropped (it cannot rotate through itself), unknown
    /// names are dropped, repeats are collapsed. An empty result means "use the universal set".
    /// </summary>
    public static IReadOnlyList<string> Expand(IEnumerable<string> lines)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var rotation = new List<string>();
        foreach (string raw in lines)
        {
            string line = raw.Trim();
            if (line.Length == 0 || line[0] == '#') continue;
            if (line == BuiltInStrategies.Auto) continue;
            if (!BuiltInStrategies.IsBuiltIn(line)) continue;
            if (seen.Add(line)) rotation.Add(line);
        }
        return rotation.Count > 0 ? rotation : BuiltInStrategies.Rotation;
    }

    /// <summary>
    /// Providers that have their own rotation file, by name, so the application can offer them. The universal set is
    /// always first: it is what any other name resolves to.
    /// </summary>
    public static IReadOnlyList<string> Providers(string strategiesDir)
    {
        var names = new List<string> { Universal };
        try
        {
            if (Directory.Exists(strategiesDir))
            {
                foreach (string file in Directory.EnumerateFiles(strategiesDir, "*" + Extension))
                {
                    string name = Path.GetFileName(file)[..^Extension.Length];
                    if (IsValidProvider(name) && name != Universal) names.Add(name);
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // an unreadable folder is just "no providers beyond the universal one"
        }
        names.Sort(StringComparer.Ordinal);
        names.Remove(Universal);
        names.Insert(0, Universal);
        return names;
    }

    private static IReadOnlyList<string> ReadLines(string file)
    {
        try
        {
            return File.Exists(file) ? File.ReadAllLines(file) : BuiltInStrategies.Rotation;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return BuiltInStrategies.Rotation;
        }
    }
}
