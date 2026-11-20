#if WINDOWS
using Avalonia.Platform;
using Microsoft.Win32;
using TgwsOne.Core;
using Windows.Data.Xml.Dom;
using Windows.UI.Notifications;

namespace TgwsOne.Services;

/// <summary>
/// A WinRT toast. An unpackaged exe has no identity of its own for the notification area, so it registers one under
/// <c>HKCU\Software\Classes\AppUserModelId</c> (a name, a display name and an icon; no shortcut in the Start menu needed) and
/// shows the toast under that name.
/// </summary>
internal sealed class WindowsToastSink(AppPaths paths) : INotificationSink
{
    internal const string Aumid = "TgwsOne.App";

    public Task ShowAsync(string title, string body, CancellationToken ct = default)
    {
        RegisterIdentity();

        XmlDocument xml = ToastNotificationManager.GetTemplateContent(ToastTemplateType.ToastText02);
        XmlNodeList texts = xml.GetElementsByTagName("text");
        texts[0].AppendChild(xml.CreateTextNode(title));
        texts[1].AppendChild(xml.CreateTextNode(body));
        ToastNotificationManager.CreateToastNotifier(Aumid).Show(new ToastNotification(xml));
        return Task.CompletedTask;
    }

    private void RegisterIdentity()
    {
        string icon = Path.Combine(paths.DataDir, "tgwsone.ico");
        if (!File.Exists(icon))
        {
            Directory.CreateDirectory(paths.DataDir);
            using Stream asset = AssetLoader.Open(new Uri("avares://TgwsOne/Assets/app.ico"));
            using FileStream file = File.Create(icon);
            asset.CopyTo(file);
        }

        using RegistryKey key = Registry.CurrentUser.CreateSubKey(@"Software\Classes\AppUserModelId\" + Aumid);
        key.SetValue("DisplayName", "TgwsOne");
        key.SetValue("IconUri", icon);
    }
}
#endif
