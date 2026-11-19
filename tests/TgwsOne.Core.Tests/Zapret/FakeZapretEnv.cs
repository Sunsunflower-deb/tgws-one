using System.Diagnostics;
using TgwsOne.Core.Zapret;

namespace TgwsOne.Core.Tests.Zapret;

/// <summary>
/// The FakeZapret stand-in plus the files it communicates through (FAKEZ_CTL / FAKEZ_ARGS / FAKEZ_HUP), set as
/// process-wide environment variables so every child the runner starts inherits them. Tests that use it share the
/// non-parallel <see cref="EnvCollection"/>. On dispose the variables are restored and leftover fakes are killed.
/// </summary>
internal sealed class FakeZapretEnv : IDisposable
{
    private readonly TempDir _tmp = new();
    private readonly Dictionary<string, string?> _saved = [];

    public FakeZapretEnv()
    {
        Ctl = _tmp.Combine("ctl");
        ArgsFile = _tmp.Combine("args");
        HupFile = _tmp.Combine("hup");
        LogFile = _tmp.Combine("logs", "zapret.log");
        ServiceLog = _tmp.Combine("logs", "service.log");
        Set("FAKEZ_CTL", Ctl);
        Set("FAKEZ_ARGS", ArgsFile);
        Set("FAKEZ_HUP", HupFile);
    }

    public string Ctl { get; }
    public string ArgsFile { get; }
    public string HupFile { get; }
    public string LogFile { get; }
    public string ServiceLog { get; }
    public string Path => _tmp.Path;
    public string Combine(params string[] parts) => _tmp.Combine(parts);

    public static string ExePath { get; } = Locate();

    /// <summary>Short delays, three crashes in five seconds make an error, a child counts as started after 250 ms.</summary>
    public static BackoffPolicy FastPolicy { get; } = new(
        [TimeSpan.FromMilliseconds(50), TimeSpan.FromMilliseconds(100), TimeSpan.FromMilliseconds(200)],
        SteadyAfter: TimeSpan.FromSeconds(30), MaxCrashes: 3, CrashWindow: TimeSpan.FromSeconds(5),
        ErrorRetry: TimeSpan.FromMilliseconds(300), StartupGrace: TimeSpan.FromMilliseconds(250));

    public ZapretRunner NewRunner(NftManager? nft = null, BackoffPolicy? policy = null, string? exe = null)
        => new(exe ?? ExePath, LogFile, new AppLog(ServiceLog), nft, policy ?? FastPolicy);

    /// <summary>Replaces the control file (an empty call clears every setting).</summary>
    public void WriteCtl(params string[] lines) => File.WriteAllLines(Ctl, lines);

    /// <summary>The argv of the most recent real run.</summary>
    public string[] Args => ReadLines(ArgsFile);

    public int Hups => ReadLines(HupFile).Count(l => l == "HUP");

    public string Log => File.Exists(LogFile) ? File.ReadAllText(LogFile) : "";

    public string ServiceLogText => File.Exists(ServiceLog) ? File.ReadAllText(ServiceLog) : "";

    private static string[] ReadLines(string file)
    {
        try { return File.Exists(file) ? File.ReadAllLines(file) : []; }
        catch (IOException) { return []; }   // being rewritten right now
    }

    /// <summary>Alive and not a zombie.</summary>
    public static bool IsAlive(int pid)
    {
        try
        {
            string stat = File.ReadAllText($"/proc/{pid}/stat");
            return stat[(stat.LastIndexOf(')') + 2)..].StartsWith('Z') == false;
        }
        catch (IOException) { return false; }
    }

    private void Set(string name, string? value)
    {
        if (!_saved.ContainsKey(name)) _saved[name] = Environment.GetEnvironmentVariable(name);
        Environment.SetEnvironmentVariable(name, value);
    }

    private static string Locate()
    {
        string exe = OperatingSystem.IsWindows() ? "FakeZapret.exe" : "FakeZapret";
        string next = System.IO.Path.Combine(AppContext.BaseDirectory, exe);
        if (File.Exists(next)) return next;

        var dir = new DirectoryInfo(AppContext.BaseDirectory.TrimEnd(System.IO.Path.DirectorySeparatorChar));
        string config = dir.Parent!.Name;
        string fallback = System.IO.Path.GetFullPath(System.IO.Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "FakeZapret", "bin", config, "net10.0", exe));
        if (File.Exists(fallback)) return fallback;
        throw new FileNotFoundException("FakeZapret apphost not found (looked next to the tests and in " + fallback + ")");
    }

    public void Dispose()
    {
        foreach (Process p in Process.GetProcessesByName("FakeZapret"))
        {
            try
            {
                if (string.Equals(p.MainModule?.FileName, ExePath, StringComparison.Ordinal))
                {
                    p.Kill(entireProcessTree: true);
                    p.WaitForExit(2000);
                }
            }
            catch { /* gone, or not ours */ }
            p.Dispose();
        }
        foreach (var (name, value) in _saved) Environment.SetEnvironmentVariable(name, value);
        _tmp.Dispose();
    }
}
