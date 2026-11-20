using System.Diagnostics;

namespace TgwsOne.Core.Tests;

/// <summary>
/// The one way a test starts TgwsOne.dll as a child process. Since SP3 a launch without a switch opens the window, so two
/// guards live here instead of in every caller: an empty argument list is refused, and the child gets no display at all
/// (a window must never appear on the desktop of whoever runs the tests; a UI launch would fail with "XOpenDisplay failed").
/// </summary>
internal static class TgwsOneProcess
{
    /// <summary>Output and error redirected, no shell; the caller adds its own environment variables and starts it.</summary>
    public static ProcessStartInfo StartInfo(params string[] args)
    {
        if (args.Length == 0)
            throw new ArgumentException("a test must pass an explicit switch: no arguments start the window", nameof(args));

        var psi = new ProcessStartInfo(Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") ?? "dotnet")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        psi.ArgumentList.Add(Path.Combine(AppContext.BaseDirectory, "TgwsOne.dll"));
        foreach (string a in args) psi.ArgumentList.Add(a);
        psi.Environment.Remove("DISPLAY");
        psi.Environment.Remove("WAYLAND_DISPLAY");
        psi.Environment.Remove("DBUS_SESSION_BUS_ADDRESS");   // no notifications or tray icon on the real desktop either
        return psi;
    }
}
