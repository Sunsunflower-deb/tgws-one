namespace TgwsOne.Mobile;

/// <summary>
/// The files the app keeps in its private directory. On Android that directory is <c>Context.FilesDir</c>; the
/// engine and the settings store only need paths, so this mapping stays testable without an Android device.
/// </summary>
public sealed record MobileStorage(string IniPath, string AppLogPath, string ProxyLogPath)
{
    public static MobileStorage For(string filesDirectory) => new(
        IniPath: Path.Combine(filesDirectory, "tgwsone.ini"),
        AppLogPath: Path.Combine(filesDirectory, "logs", "app.log"),
        ProxyLogPath: Path.Combine(filesDirectory, "logs", "proxy.log"));
}
