namespace TgwsOne.Services;

internal enum CloseAction
{
    /// <summary>Let the window close (the application is quitting, or something other than the user's click asked for it).</summary>
    Allow,

    /// <summary>Hide the window; the tray icon stays and the application keeps running.</summary>
    HideToTray,

    /// <summary>Minimize it: there is no tray to hide to, and an invisible application cannot be found again.</summary>
    Minimize,
}

/// <summary>
/// What the window's × does. The application is a tray one: closing the window does not end it (that is «Выход»). Without a tray
/// the window is minimized instead, and the first time it goes to the tray the user is told where it went, once for good.
/// </summary>
internal sealed class CloseBehavior(bool trayAvailable, IAppSettings settings, Notifier notifier)
{
    /// <summary>Set when the application is on its way out: from then on every close goes through.</summary>
    public bool Quitting { get; set; }

    /// <param name="byUser">The user closed the window (the ×, Alt+F4, the window manager's close), not the application shutting it down.</param>
    public CloseAction OnClosing(bool byUser)
    {
        if (Quitting || !byUser) return CloseAction.Allow;
        if (!trayAvailable) return CloseAction.Minimize;

        if (!settings.TrayHintShown)
        {
            settings.MarkTrayHintShown();
            notifier.Notify(NoticeKind.TrayHint, Notifier.TrayHintTitle, Notifier.TrayHintText);
        }
        return CloseAction.HideToTray;
    }
}
