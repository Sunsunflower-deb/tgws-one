using Tmds.DBus.Protocol;
using TgwsOne.Core;

namespace TgwsOne.Services;

/// <summary>A desktop with nothing to show notifications on (or a platform we have no way for): they are dropped.</summary>
internal sealed class NullNotificationSink : INotificationSink
{
    public Task ShowAsync(string title, string body, CancellationToken ct = default) => Task.CompletedTask;
}

internal static class NotificationSinks
{
    public static INotificationSink ForThisMachine(AppPaths paths)
    {
#if WINDOWS
        return new WindowsToastSink(paths);
#else
        _ = paths;
        return OperatingSystem.IsLinux() ? new LinuxNotificationSink() : new NullNotificationSink();
#endif
    }
}

/// <summary>The session bus of this user, as the D-Bus helpers here connect to it.</summary>
internal static class SessionBus
{
    /// <summary>The address from the environment, or the systemd user bus where there is one; null when there is no session bus at all.</summary>
    public static string? Address()
    {
        if (Environment.GetEnvironmentVariable("DBUS_SESSION_BUS_ADDRESS") is { Length: > 0 } address) return address;
        string? runtime = Environment.GetEnvironmentVariable("XDG_RUNTIME_DIR");
        return runtime is { Length: > 0 } && File.Exists(Path.Combine(runtime, "bus")) ? $"unix:path={Path.Combine(runtime, "bus")}" : null;
    }

    /// <summary>A connection of our own (the call is rare enough not to keep one open), already connected; null without a bus.</summary>
    public static async Task<DBusConnection?> ConnectAsync(string? address, CancellationToken ct)
    {
        address ??= Address();
        if (address == null) return null;
        var connection = new DBusConnection(address);
        try
        {
            await connection.ConnectAsync().AsTask().WaitAsync(ct).ConfigureAwait(false);
            return connection;
        }
        catch
        {
            connection.Dispose();
            throw;
        }
    }
}

/// <summary>
/// <c>org.freedesktop.Notifications.Notify</c> over the session bus, which every desktop's notification daemon answers (dunst,
/// the GNOME and KDE shells, mako, …). A desktop with no daemon answers with an error, and that is silently the end of it.
/// </summary>
internal sealed class LinuxNotificationSink(string? busAddress = null) : INotificationSink
{
    internal const string Service = "org.freedesktop.Notifications";
    internal const string AppName = "TgwsOne";
    internal const string IconName = "tgwsone";

    public async Task ShowAsync(string title, string body, CancellationToken ct = default)
    {
        try
        {
            using DBusConnection? connection = await SessionBus.ConnectAsync(busAddress, ct).ConfigureAwait(false);
            if (connection == null) return;

            // Notify(app_name s, replaces_id u, app_icon s, summary s, body s, actions as, hints a{sv}, expire_timeout i) -> u
            MessageBuffer message;
            using (MessageWriter writer = connection.GetMessageWriter())
            {
                writer.WriteMethodCallHeader(destination: Service, path: "/org/freedesktop/Notifications", @interface: Service,
                                             member: "Notify", signature: "susssasa{sv}i");
                writer.WriteString(AppName);
                writer.WriteUInt32(0);
                writer.WriteString(IconName);
                writer.WriteString(title);
                writer.WriteString(body);
                ArrayStart actions = writer.WriteArrayStart(DBusType.String);
                writer.WriteArrayEnd(actions);
                ArrayStart hints = writer.WriteDictionaryStart();
                writer.WriteDictionaryEnd(hints);
                writer.WriteInt32(-1);   // the daemon's own timeout
                message = writer.CreateMessage();
            }

            await connection.CallMethodAsync(message).WaitAsync(ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is DBusExceptionBase or IOException or System.Net.Sockets.SocketException)
        {
            // no session bus, or no notification daemon on it (or it refused): nothing to be done about it
        }
    }
}

/// <summary>Whether this desktop has somewhere to put a tray icon.</summary>
internal static class TrayAvailability
{
    internal const string Watcher = "org.kde.StatusNotifierWatcher";

    /// <summary>Windows always; Linux only if something owns <c>org.kde.StatusNotifierWatcher</c> on the session bus (without it the icon would be created and never seen).</summary>
    public static async Task<bool> CheckAsync(string? busAddress = null, CancellationToken ct = default)
    {
        if (OperatingSystem.IsWindows()) return true;
        if (!OperatingSystem.IsLinux()) return false;
        try
        {
            using DBusConnection? connection = await SessionBus.ConnectAsync(busAddress, ct).ConfigureAwait(false);
            if (connection == null) return false;
            string[] names = await connection.ListServicesAsync().WaitAsync(ct).ConfigureAwait(false);
            return names.Contains(Watcher);
        }
        catch (Exception ex) when (ex is DBusExceptionBase or IOException or System.Net.Sockets.SocketException or OperationCanceledException or TimeoutException)
        {
            return false;
        }
    }
}
