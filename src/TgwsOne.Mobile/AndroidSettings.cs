using System.Globalization;
using TgwsOne.Core;

namespace TgwsOne.Mobile;

/// <summary>
/// What the Android build keeps in the <c>[android]</c> section. <see cref="PoolIdle"/> is the seconds an idle pool
/// stays warm; the engine side of it arrives with the battery task (E3.5), the setting itself is stored from the start.
/// </summary>
public sealed record AndroidSettings(string Strategy, string Provider, int PoolIdle)
{
    public const string DefaultStrategy = "auto";
    public const string DefaultProvider = "";
    public const int DefaultPoolIdle = 180;

    public static AndroidSettings Default => new(DefaultStrategy, DefaultProvider, DefaultPoolIdle);
}

/// <summary>
/// Reads and writes <c>[android] strategy|provider|poolidle</c>. A missing or malformed value falls back to its
/// default, the same way <c>ProxySettingsStore</c> treats a broken <c>[proxy]</c> value, so a hand-edited INI can
/// never leave the app without a usable choice.
/// </summary>
public static class AndroidSettingsStore
{
    public const string Section = "android";

    // Stable ids from the plan's strategy table (E3.3). An unknown id is a typo, not a new strategy.
    private static readonly HashSet<string> Strategies = new(StringComparer.Ordinal)
    {
        "auto", "fake", "split-sni", "split-midsni", "fake-split",
        "disorder-1", "disorder-sld", "oob-sni", "tlsrec-midsni",
    };

    public static AndroidSettings Load(IniFile ini)
    {
        string strategy = ini.Get(Section, "strategy") ?? "";
        if (!Strategies.Contains(strategy)) strategy = AndroidSettings.DefaultStrategy;

        string provider = ini.Get(Section, "provider") ?? "";
        if (!IsValidProvider(provider)) provider = AndroidSettings.DefaultProvider;

        string rawIdle = ini.Get(Section, "poolidle") ?? "";
        int poolIdle = int.TryParse(rawIdle, NumberStyles.Integer, CultureInfo.InvariantCulture, out int value)
                       && value >= 0
            ? value
            : AndroidSettings.DefaultPoolIdle;

        return new AndroidSettings(strategy, provider, poolIdle);
    }

    public static void Save(IniFile ini, AndroidSettings settings)
    {
        ini.Set(Section, "strategy", settings.Strategy);
        ini.Set(Section, "provider", settings.Provider);
        ini.Set(Section, "poolidle", settings.PoolIdle.ToString(CultureInfo.InvariantCulture));
        ini.Save();
    }

    /// <summary><c>^[a-z0-9][a-z0-9._-]{0,31}$</c>, the same names the rotation files use.</summary>
    public static bool IsValidProvider(string name)
    {
        if (name.Length is 0 or > 32) return false;
        if (!char.IsAsciiLetterLower(name[0]) && !char.IsAsciiDigit(name[0])) return false;
        return name.All(c => char.IsAsciiLetterLower(c) || char.IsAsciiDigit(c) || c is '.' or '_' or '-');
    }
}
