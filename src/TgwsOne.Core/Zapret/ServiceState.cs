namespace TgwsOne.Core.Zapret;

/// <summary>What the service remembers across restarts: whether the bypass is on and which strategy it uses.</summary>
public sealed record ServiceState(bool Enabled, string Strategy)
{
    private const string Section = "zapret";

    /// <summary>
    /// A missing file, key or unreadable value means the default: enabled, <c>auto</c>. A strategy ID is kept as written
    /// (it may name a custom strategy that is gone, or one a newer version knows): the service decides what to do with it.
    /// </summary>
    public static ServiceState Load(string iniPath)
    {
        var ini = IniFile.Load(iniPath);
        string? enabled = ini.Get(Section, "enabled")?.Trim();
        string? strategy = ini.Get(Section, "strategy")?.Trim();
        return new ServiceState(
            Enabled: !(enabled == "0" || string.Equals(enabled, "false", StringComparison.OrdinalIgnoreCase)),
            Strategy: string.IsNullOrEmpty(strategy) ? BuiltInStrategies.Auto : strategy);
    }

    /// <summary>Rewrites the <c>[zapret]</c> section; anything else in the file stays.</summary>
    public void Save(string iniPath)
    {
        var ini = IniFile.Load(iniPath);
        ini.Set(Section, "enabled", Enabled ? "1" : "0");
        ini.Set(Section, "strategy", Strategy);
        ini.Save();
    }
}
