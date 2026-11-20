using TgwsOne.Core;

namespace TgwsOne.Services;

/// <summary>The real <see cref="IAppSettings"/>: the INI under the app's data directory.</summary>
internal sealed class IniAppSettings(AppPaths paths, AppLog? log = null) : IAppSettings
{
    public ProxySettings Load() => ProxySettingsStore.Load(IniFile.Load(paths.Ini));

    public string TelegramLink()
    {
        IniFile ini = IniFile.Load(paths.Ini);
        string secret = ProxySettingsStore.EnsureSecret(ini, log);
        ProxySettings s = ProxySettingsStore.Load(ini);
        return Core.TelegramLink.Build(s.Host, s.Port, secret);
    }

    public void ReplaceSecret()
    {
        IniFile ini = IniFile.Load(paths.Ini);
        ini.Set("app", "secret", ProxySettingsStore.NewSecret());
        ini.Save();
        log?.Write("secret заменён по просьбе пользователя");
    }

    public ProxyForm ReadForm() => ProxySettingsStore.ReadForm(IniFile.Load(paths.Ini));

    public string Secret() => ProxySettingsStore.EnsureSecret(IniFile.Load(paths.Ini), log);

    public bool FirstRunDone => IniFile.Load(paths.Ini).Get("app", "firstrun") == "done";

    public void MarkFirstRunDone()
    {
        IniFile ini = IniFile.Load(paths.Ini);
        ini.Set("app", "firstrun", "done");
        ini.Save();
    }

    public bool TrayHintShown => IniFile.Load(paths.Ini).Get("app", "trayhint") == "shown";

    public void MarkTrayHintShown()
    {
        IniFile ini = IniFile.Load(paths.Ini);
        ini.Set("app", "trayhint", "shown");
        ini.Save();
    }

    public bool LanSharing => LanShare.IsEnabled(IniFile.Load(paths.Ini));

    public string? LanAddress => IniFile.Load(paths.Ini).Get("app", LanShare.AddressKey) is { Length: > 0 } a ? a : null;

    public string PreviousHost => LanShare.PreviousHost(IniFile.Load(paths.Ini));

    public void Save(ProxySettings settings, bool lanSharing, string? lanAddress, string? previousHost = null)
    {
        IniFile ini = IniFile.Load(paths.Ini);
        // The switch first: turning sharing on has to see the host the proxy listens on now (or the one the form had), before
        // the form's values overwrite it.
        if (lanSharing)
        {
            if (previousHost is { Length: > 0 } host && host != LanShare.WildcardHost) ini.Set("proxy", "host", host);
            LanShare.Enable(ini);
        }
        else
        {
            LanShare.Disable(ini);
        }

        ProxySettings toWrite = lanSharing ? settings with { Host = System.Net.IPAddress.Any } : settings;
        ini.Set("app", LanShare.AddressKey, lanSharing ? lanAddress ?? "" : "");
        ProxySettingsStore.Save(ini, toWrite);   // also saves the file
        log?.Write(lanSharing ? $"настройки сохранены (раздача в сеть включена, адрес {lanAddress})" : "настройки сохранены");
    }
}
