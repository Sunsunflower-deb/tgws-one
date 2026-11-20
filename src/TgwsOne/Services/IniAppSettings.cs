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
}
