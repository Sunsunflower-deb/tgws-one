using TgwsOne.Core;

namespace TgwsOne.Mobile;

/// <summary>
/// What the mobile pages need from the settings file. Every call reads the file again: a start that had to move
/// ports rewrites it behind the pages' back, and the Android section is the single place the chosen strategy lives.
/// </summary>
public interface IMobileSettings
{
    /// <summary>The <c>[android]</c> section with defaults where a value is missing or wrong; never throws.</summary>
    AndroidSettings LoadAndroid();

    /// <summary>Writes <c>[android] strategy|provider|poolidle</c>.</summary>
    void SaveAndroid(AndroidSettings settings);

    /// <summary>The proxy settings as they are on disk now, for the address the status line shows.</summary>
    ProxySettings LoadProxy();

    /// <summary>The <c>tg://proxy</c> link for Telegram; makes the secret first if there is none yet.</summary>
    string TelegramLink();
}

/// <summary>The real files: the INI in the app's private directory, read and written through the shared stores.</summary>
public sealed class FileMobileSettings(MobileStorage storage) : IMobileSettings
{
    public AndroidSettings LoadAndroid() => AndroidSettingsStore.Load(IniFile.Load(storage.IniPath));

    public void SaveAndroid(AndroidSettings settings) => AndroidSettingsStore.Save(IniFile.Load(storage.IniPath), settings);

    public ProxySettings LoadProxy() => ProxySettingsStore.Load(IniFile.Load(storage.IniPath));

    public string TelegramLink()
    {
        IniFile ini = IniFile.Load(storage.IniPath);
        string secret = ProxySettingsStore.EnsureSecret(ini);
        ProxySettings settings = ProxySettingsStore.Load(ini);
        return TgwsOne.Core.TelegramLink.Build(settings.Host, settings.Port, secret);
    }
}

/// <summary>Opens a URL in whatever the system uses for it (Telegram for <c>tg://</c>).</summary>
public interface ILauncher
{
    /// <returns>False when nothing could be started for the URL.</returns>
    Task<bool> OpenAsync(string url);
}

public interface IClipboardService
{
    Task SetTextAsync(string text);
}

/// <summary>Sends text to whatever can share it (Android's <c>ACTION_SEND</c> chooser).</summary>
public interface IShareService
{
    Task ShareAsync(string text);
}

/// <summary>Something that happens every so often; tests make time pass by hand.</summary>
public interface ITicker
{
    /// <returns>Disposing it stops the ticks.</returns>
    IDisposable Start(TimeSpan period, Action tick);
}

/// <summary>A timer for the log page when the app runs for real.</summary>
public sealed class SystemTicker : ITicker
{
    public IDisposable Start(TimeSpan period, Action tick)
    {
        var stop = new CancellationTokenSource();
        _ = LoopAsync(period, tick, stop.Token);
        return stop;
    }

    private static async Task LoopAsync(TimeSpan period, Action tick, CancellationToken ct)
    {
        using var timer = new PeriodicTimer(period);
        try
        {
            while (await timer.WaitForNextTickAsync(ct).ConfigureAwait(false)) tick();
        }
        catch (OperationCanceledException)
        {
            // stopping the page disposes the token source: that is the normal end of the loop
        }
    }
}

/// <summary>The buttons under a diagnostics verdict, implemented by the page that owns the real actions.</summary>
public interface IDiagnosticsActions
{
    Task<string?> ChangePortAsync();
    Task<string?> ConnectAsync();
    Task<string?> OpenInTelegramAsync();
    Task<string?> EnableBypassAsync();
    Task<string?> FindStrategyAsync();
}
