namespace TgwsOne.Core;

public enum OsKind { Windows, Linux }

/// <summary>
/// Per-OS file locations. Nothing here touches the disk: directories are created lazily by the writers.
/// Paths are joined with the target OS separator so Windows layouts can be unit-tested on Linux.
/// </summary>
public sealed class AppPaths
{
    public string AppDir { get; }
    public string Engine { get; }

    /// <summary>True when <c>TGWS_ENGINE</c> named the engine (tests, a custom build). Otherwise <see cref="Engine"/> is this executable.</summary>
    public bool EngineOverridden { get; }

    public string DataDir { get; }
    public string Ini { get; }
    public string ListsDir { get; }
    public string LogsDir { get; }
    public string RunDir { get; }
    public string PidFile { get; }
    public string StartLockFile { get; }

    /// <summary>The window's control socket (Unix) or the seed of its pipe name (Windows); see <see cref="EngineControlServer"/>.</summary>
    public string ControlSocket { get; }
    public string AppLogFile { get; }
    public string ProxyLogFile { get; }
    public string EngineConsoleLog { get; }

    // Evaluated on every access: tests (and the CLI) may change TGWS_* variables in-process.
    public static AppPaths Current => For(
        OperatingSystem.IsWindows() ? OsKind.Windows : OsKind.Linux,
        Environment.GetEnvironmentVariable,
        AppContext.BaseDirectory,
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        CurrentExecutable());

    // `dotnet run`/`dotnet TgwsOne.dll` report dotnet as the process; the detached engine must still be *this*
    // program, so prefer the apphost next to the assembly when there is one.
    private static string? CurrentExecutable()
    {
        string? process = Environment.ProcessPath;
        if (string.IsNullOrEmpty(process)) return null;
        if (!string.Equals(Path.GetFileNameWithoutExtension(process), "dotnet", StringComparison.OrdinalIgnoreCase))
            return process;
        string candidate = Path.Combine(AppContext.BaseDirectory,
            OperatingSystem.IsWindows() ? "TgwsOne.exe" : "TgwsOne");
        return File.Exists(candidate) ? candidate : process;
    }

    /// <param name="defaultEngine">Where the engine lives unless <c>TGWS_ENGINE</c> says otherwise. The application
    /// passes its own executable (the engine is a mode of it); tests leave this null and get the usual file name.</param>
    public static AppPaths For(OsKind os, Func<string, string?> env, string appDir, string home,
        string? defaultEngine = null)
        => new(os, env, appDir, home, defaultEngine);

    private AppPaths(OsKind os, Func<string, string?> env, string appDir, string home, string? defaultEngine = null)
    {
        bool win = os == OsKind.Windows;
        char sep = win ? '\\' : '/';
        string Join(string first, params string[] rest)
        {
            string r = first;
            foreach (string part in rest)
                r = (r.EndsWith(sep) ? r : r + sep) + part;
            return r;
        }
        string Env(string name) => env(name) is { Length: > 0 } v ? v : "";
        // XDG: an unset, empty or relative value is invalid and ignored.
        string Xdg(string name, string fallback) => Env(name) is { Length: > 0 } v && v.StartsWith('/') ? v : fallback;

        AppDir = appDir.Length > 1 ? appDir.TrimEnd(sep) : appDir;
        string engineOverride = Env("TGWS_ENGINE");
        EngineOverridden = engineOverride.Length > 0;
        Engine = EngineOverridden
            ? engineOverride
            : defaultEngine is { Length: > 0 } ? defaultEngine : Join(AppDir, win ? "TgwsOne.exe" : "tgwsone");

        string iniName = win ? "TgwsOne.ini" : "tgwsone.ini";
        string state = win ? "" : Join(Xdg("XDG_STATE_HOME", Join(home, ".local", "state")), "tgwsone");

        if (Env("TGWS_DATA_DIR") is { Length: > 0 } over)
        {
            DataDir = over;
            LogsDir = Join(over, "logs");
            RunDir = Join(over, "run");
        }
        else if (win)
        {
            string local = Env("LOCALAPPDATA") is { Length: > 0 } l ? l : Join(home, "AppData", "Local");
            DataDir = Join(local, "TgwsOne");
            LogsDir = Join(DataDir, "logs");
            RunDir = Join(DataDir, "run");
        }
        else
        {
            DataDir = Join(Xdg("XDG_CONFIG_HOME", Join(home, ".config")), "tgwsone");
            LogsDir = Join(state, "logs");
            RunDir = Xdg("XDG_RUNTIME_DIR", "") is { Length: > 0 } rt ? Join(rt, "tgwsone") : Join(state, "run");
        }

        Ini = Join(DataDir, iniName);
        ListsDir = Join(DataDir, "lists");
        PidFile = Join(RunDir, "engine.pid");
        StartLockFile = Join(RunDir, "start.lock");
        ControlSocket = Join(RunDir, "engine.sock");
        AppLogFile = Join(LogsDir, "app.log");
        ProxyLogFile = Join(LogsDir, "proxy.log");
        EngineConsoleLog = Join(LogsDir, "engine-console.log");
    }
}
