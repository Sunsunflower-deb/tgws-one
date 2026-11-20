using System.Diagnostics;

namespace TgwsOne.Core.Tests;

/// <summary>
/// The one way a test starts TgwsOne.dll as a child process. Guards live here instead of in every caller: an empty argument
/// list is refused (since SP3 a launch without a switch opens the window), the child gets no display and no session bus (a window,
/// a notification or a tray icon must never appear on the desktop of whoever runs the tests), and every directory the child
/// would write to is a throwaway one (a --selftest or --start that forgot its own TGWS_DATA_DIR once wrote into the real
/// profile). A test that needs a particular directory sets its own variable on the returned info; that wins.
/// </summary>
internal static class TgwsOneProcess
{
    private static readonly Lazy<string> SandboxRoot = new(() =>
    {
        string root = Path.Combine(Path.GetTempPath(), $"tgwsone-tests-{Environment.ProcessId}-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        AppDomain.CurrentDomain.ProcessExit += (_, _) =>
        {
            try { Directory.Delete(root, recursive: true); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { /* the temp directory's own cleaner gets it */ }
        };
        return root;
    });

    /// <summary>The directory the children's default profile lives in (for a test that looks at what a child wrote there).</summary>
    public static string Sandbox => SandboxRoot.Value;

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

        string root = Sandbox;
        psi.Environment["TGWS_DATA_DIR"] = Path.Combine(root, "data");
        psi.Environment["TGWS_SERVICE_ROOT"] = Path.Combine(root, "service");
        foreach ((string variable, string directory) in new[]
                 {
                     ("XDG_CONFIG_HOME", "config"), ("XDG_STATE_HOME", "state"), ("XDG_DATA_HOME", "data-home"),
                     ("XDG_CACHE_HOME", "cache"), ("XDG_RUNTIME_DIR", "run"),
                 })
        {
            string path = Path.Combine(root, directory);
            Directory.CreateDirectory(path);
            if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            psi.Environment[variable] = path;
        }
        return psi;
    }
}
