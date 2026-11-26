using System.Reflection;
using System.Text.RegularExpressions;

namespace TgwsOne.Engine.Desync;

/// <summary>
/// The rotations that ship inside the engine. They use the same file format as the desktop strategies folder:
/// one built-in strategy ID per line, <c>#</c> starts a comment. An unknown operator, an unreadable or empty file
/// all fall back to <see cref="BuiltInDesync.BuiltIn"/>: a missing operator must never leave the phone without a
/// working bypass.
/// </summary>
public static partial class DesyncRotations
{
    /// <summary>The default set every other operator falls back to.</summary>
    public const string Universal = "universal";

    public const string Extension = ".rotation";

    private const string ResourcePrefix = "TgwsOne.Engine.Desync.Rotations.";
    private static readonly Lazy<IReadOnlyDictionary<string, string>> OperatorsByMccMnc = new(LoadMccMnc);

    [GeneratedRegex(@"^[a-z0-9][a-z0-9._-]{0,31}\z", RegexOptions.CultureInvariant)]
    private static partial Regex OperatorRegex();

    /// <summary>Lower-case letters, digits, <c>.</c>, <c>_</c>, <c>-</c>; at most 32 characters.</summary>
    public static bool IsValidOperator(string? name)
        => name is not null && OperatorRegex().IsMatch(name);

    /// <summary>The rotation for an operator; unknown, empty and unreadable names use the universal set.</summary>
    public static IReadOnlyList<DesyncStrategy> ForOperator(string? operatorName)
    {
        string name = CanonicalOperator(operatorName);
        try
        {
            string[]? lines = ReadResource(name + Extension);
            return lines is null ? BuiltInDesync.BuiltIn : Parse(lines);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            return BuiltInDesync.BuiltIn;
        }
    }

    /// <summary>The operator name as stored (a known resource), or <see cref="Universal"/>.</summary>
    public static string CanonicalOperator(string? operatorName)
    {
        if (!IsValidOperator(operatorName)) return Universal;
        if (operatorName == Universal) return Universal;
        try
        {
            return ReadResource(operatorName! + Extension) is null ? Universal : operatorName!;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            return Universal;
        }
    }

    /// <summary>
    /// Keeps known built-in IDs in file order: unknown names, <c>auto</c> and repeats are dropped. Nothing usable
    /// means the universal set, exactly like the desktop <c>ProviderStrategies.Expand</c>.
    /// </summary>
    public static IReadOnlyList<DesyncStrategy> Parse(IEnumerable<string> lines)
    {
        ArgumentNullException.ThrowIfNull(lines);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var rotation = new List<DesyncStrategy>();
        foreach (string raw in lines)
        {
            string line = raw.Trim();
            if (line.Length == 0 || line[0] == '#') continue;
            DesyncStrategy? strategy = BuiltInDesync.Find(line);
            if (strategy is null) continue;
            if (seen.Add(strategy.Id)) rotation.Add(strategy);
        }
        return rotation.Count > 0 ? rotation : BuiltInDesync.BuiltIn;
    }

    /// <summary>Operator names with a shipped rotation, the universal one first.</summary>
    public static IReadOnlyList<string> Operators
    {
        get
        {
            var names = new List<string>();
            foreach (string resource in typeof(DesyncRotations).Assembly.GetManifestResourceNames())
            {
                if (!resource.StartsWith(ResourcePrefix, StringComparison.Ordinal) ||
                    !resource.EndsWith(Extension, StringComparison.Ordinal))
                {
                    continue;
                }
                string name = resource[ResourcePrefix.Length..^Extension.Length];
                if (IsValidOperator(name)) names.Add(name);
            }
            names.Sort(StringComparer.Ordinal);
            names.Remove(Universal);
            names.Insert(0, Universal);
            return names;
        }
    }

    /// <summary>The operator a device's MCC/MNC belongs to, or null when the table does not know it.</summary>
    public static string? OperatorForMccMnc(string? mccMnc)
    {
        if (string.IsNullOrWhiteSpace(mccMnc)) return null;
        return OperatorsByMccMnc.Value.GetValueOrDefault(mccMnc.Trim());
    }

    /// <summary>The rotation for a device's MCC/MNC; unknown codes use the universal set.</summary>
    public static IReadOnlyList<DesyncStrategy> ForMccMnc(string? mccMnc)
        => ForOperator(OperatorForMccMnc(mccMnc));

    private static string[]? ReadResource(string fileName)
    {
        Assembly assembly = typeof(DesyncRotations).Assembly;
        using Stream? stream = assembly.GetManifestResourceStream(ResourcePrefix + fileName);
        if (stream is null) return null;
        using var reader = new StreamReader(stream);
        var lines = new List<string>();
        string? line;
        while ((line = reader.ReadLine()) is not null) lines.Add(line);
        return [.. lines];
    }

    private static IReadOnlyDictionary<string, string> LoadMccMnc()
    {
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        try
        {
            string[]? lines = ReadResource("mcc-mnc.txt");
            if (lines is null) return map;
            foreach (string raw in lines)
            {
                string line = raw.Trim();
                if (line.Length == 0 || line[0] == '#') continue;
                int space = line.IndexOf(' ');
                if (space <= 0) continue;
                string code = line[..space];
                string name = line[(space + 1)..].Trim();
                if (name.Length == 0 || !IsValidOperator(name)) continue;
                map.TryAdd(code, name);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            // An unreadable table only means "no operator detected"; the universal set still works.
        }
        return map;
    }
}
