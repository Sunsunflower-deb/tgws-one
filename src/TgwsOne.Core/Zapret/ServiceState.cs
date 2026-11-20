namespace TgwsOne.Core.Zapret;

/// <summary>What the service remembers across restarts: whether the bypass is on, which strategy it uses and what it applies to.</summary>
public sealed record ServiceState(bool Enabled, string Strategy, ZapretScope Scope = ZapretScope.Telegram)
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
            Strategy: string.IsNullOrEmpty(strategy) ? BuiltInStrategies.Auto : strategy,
            // An older 2.0.0 state file has no scope key: that install had the wide bypass, but the default now is
            // Telegram only, so a missing key means the narrow scope (the user can widen it on the page).
            Scope: ZapretScopes.Parse(ini.Get(Section, "scope")));
    }

    /// <summary>
    /// Rewrites the <c>[zapret]</c> section; anything else in the file stays. The folder is not made private (the
    /// exclusion lists next to this file must stay readable for nfqws2, which runs as an unprivileged user).
    /// </summary>
    public void Save(string iniPath)
    {
        var ini = IniFile.Load(iniPath);
        ini.Set(Section, "enabled", Enabled ? "1" : "0");
        ini.Set(Section, "strategy", Strategy);
        ini.Set(Section, "scope", Scope.ToWire());
        ini.Save(privateDirectory: false);
    }
}
