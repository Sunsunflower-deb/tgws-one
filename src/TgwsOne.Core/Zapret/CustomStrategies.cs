using System.Text;
using System.Text.RegularExpressions;

namespace TgwsOne.Core.Zapret;

/// <summary>
/// Strategies an administrator writes by hand: <c>&lt;dir&gt;/&lt;name&gt;.args</c>, one argument per line, <c>#</c> comments.
/// The file holds a desync part only. Targets, interception, debugging and <c>@file</c> are the service's business,
/// so anything but the options below is rejected (the strategy runs as root / SYSTEM).
/// </summary>
public static partial class CustomStrategies
{
    private static readonly string[] AllowedOptions = ["--lua-desync", "--blob", "--lua-init", "--payload", "--out-range", "--in-range"];

    private const int GroupOrOtherWrite = 0x12; // 0022

    [GeneratedRegex(@"^[a-z0-9-]{1,32}\z", RegexOptions.CultureInvariant)]
    private static partial Regex NameRegex();

    public static bool IsValidName(string name) => NameRegex().IsMatch(name);

    /// <summary>Checks the lines; on success the arguments go to <see cref="DesyncPart.Args"/> as written (blobs included).</summary>
    public static (DesyncPart? Part, string? Error) Parse(string text)
    {
        var args = new List<string>();
        bool hasStep = false;
        foreach (string raw in text.Split('\n'))
        {
            string line = raw.Trim();
            if (line.Length == 0 || line[0] == '#') continue;

            int eq = line.IndexOf('=');
            string option = eq < 0 ? line : line[..eq];
            if (!AllowedOptions.Contains(option, StringComparer.Ordinal))
                return (null, $"недопустимый параметр в стратегии: {option}");
            if (eq < 0 || eq == line.Length - 1)
                return (null, $"у параметра в стратегии нет значения: {option}");

            args.Add(line);
            if (option == "--lua-desync") hasStep = true;
        }
        if (!hasStep) return (null, "в стратегии нет ни одного шага --lua-desync");
        return (new DesyncPart([], args), null);
    }

    /// <summary>Names (file name without <c>.args</c>) of the files the service would accept, sorted.</summary>
    public static IReadOnlyList<string> List(string dir, OsKind os, Func<string, UnixFileMeta?>? stat = null)
    {
        var names = new List<string>();
        try
        {
            foreach (string file in Directory.EnumerateFiles(dir, "*.args"))
            {
                string name = Path.GetFileNameWithoutExtension(file);
                if (IsValidName(name) && OwnershipError(file, name, os, stat) == null) names.Add(name);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return [];   // no directory (nothing was ever added) or not readable: no custom strategies
        }
        names.Sort(StringComparer.Ordinal);
        return names;
    }

    public static (DesyncPart? Part, string? Error) Load(string dir, string name, OsKind os, Func<string, UnixFileMeta?>? stat = null)
    {
        if (!IsValidName(name)) return (null, $"недопустимое имя стратегии: {name}");
        string file = Path.Combine(dir, name + ".args");
        if (!File.Exists(file)) return (null, $"стратегия {name} не найдена");
        if (OwnershipError(file, name, os, stat) is { } rejected) return (null, rejected);

        string text;
        try
        {
            text = File.ReadAllText(file, Encoding.UTF8);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return (null, $"не удалось прочитать стратегию {name}: {ex.Message}");
        }
        return Parse(text);
    }

    // Linux: only root may have written the file (a file anyone else can change would run as root). On Windows the
    // service protects the whole directory with an ACL instead, and there is no owner to compare against.
    private static string? OwnershipError(string file, string name, OsKind os, Func<string, UnixFileMeta?>? stat)
    {
        if (os != OsKind.Linux) return null;
        UnixFileMeta? meta = (stat ?? LibC.Stat)(file);
        if (meta is { OwnerUid: 0 } && (meta.Mode & GroupOrOtherWrite) == 0) return null;
        return $"стратегия {name} отклонена: файл должен принадлежать root и не быть доступен на запись другим";
    }
}
