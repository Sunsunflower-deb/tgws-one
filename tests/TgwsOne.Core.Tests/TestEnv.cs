using System.Diagnostics;
using System.Net;
using System.Net.Sockets;

namespace TgwsOne.Core.Tests;

/// <summary>Tests that set process-wide environment variables or start engines share this non-parallel collection.</summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class EnvCollection
{
    public const string Name = "env";
}

/// <summary>
/// Isolated state for one test: a temp data dir (TGWS_DATA_DIR) and the FakeEngine as the engine (TGWS_ENGINE),
/// set for this process (the controller's child processes inherit them) and mirrored in <see cref="ChildEnv"/>
/// for explicitly spawned CLI processes. Everything is restored and any FakeEngine left behind is killed on dispose.
/// </summary>
public sealed class TestEnv : IDisposable
{
    private readonly TempDir _tmp = new();
    private readonly Dictionary<string, string?> _saved = [];
    private readonly Dictionary<string, string> _child = [];

    public TestEnv()
    {
        DataDir = _tmp.Combine("data");
        Set("TGWS_DATA_DIR", DataDir);
        Set("TGWS_ENGINE", FakeEnginePath);
        // A previous test's FAKE_* settings must not leak in.
        foreach (string name in new[] { "FAKE_MODE", "FAKE_ARGS_FILE", "FAKE_CHILD_PID_FILE" }) Set(name, null);
    }

    public string DataDir { get; }

    /// <summary>Scratch directory next to the data dir (for files that must not live in it, e.g. FAKE_ARGS_FILE).</summary>
    public string Scratch(string name) => _tmp.Combine(name);

    /// <summary>The variables set through <see cref="Set"/>, for processes started with an explicit environment.</summary>
    public IReadOnlyDictionary<string, string> ChildEnv => _child;

    public AppPaths Paths => AppPaths.Current;

    /// <summary>Sets (or, with null, removes) an environment variable for this process; restored on dispose.</summary>
    public void Set(string name, string? value)
    {
        if (!_saved.ContainsKey(name)) _saved[name] = Environment.GetEnvironmentVariable(name);
        Environment.SetEnvironmentVariable(name, value);
        if (value is null) _child.Remove(name); else _child[name] = value;
    }

    public static string FakeEnginePath { get; } = LocateFakeEngine();

    private static string LocateFakeEngine()
    {
        string exe = OperatingSystem.IsWindows() ? "FakeEngine.exe" : "FakeEngine";
        string next = Path.Combine(AppContext.BaseDirectory, exe);
        if (File.Exists(next)) return next;

        // Not copied next to the tests: use the FakeEngine project's own output (…/tests/<proj>/bin/<Config>/net10.0/).
        var dir = new DirectoryInfo(AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar));
        string config = dir.Parent!.Name;
        string fallback = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "FakeEngine", "bin", config, "net10.0", exe));
        if (File.Exists(fallback)) return fallback;
        throw new FileNotFoundException("FakeEngine apphost not found (looked next to the tests and in " + fallback + ")");
    }

    /// <summary>A port that was free a moment ago.</summary>
    public static int FreePort()
    {
        using var l = new TcpListener(IPAddress.Loopback, 0);
        l.Start();
        return ((IPEndPoint)l.LocalEndpoint).Port;
    }

    /// <summary>Binds <paramref name="count"/> consecutive loopback ports, scanning upward from 40000. The caller stops them.</summary>
    public static List<TcpListener> BindConsecutive(int count, int from = 40000)
    {
        for (int start = from; start + count - 1 <= 65535;)
        {
            var held = new List<TcpListener>();
            int failedAt = -1;
            for (int p = start; p < start + count; p++)
            {
                try
                {
                    var l = new TcpListener(IPAddress.Loopback, p);
                    l.Start();
                    held.Add(l);
                }
                catch (SocketException) { failedAt = p; break; }
            }
            if (failedAt < 0) return held;
            foreach (var l in held) l.Stop();
            start = failedAt + 1;
        }
        throw new InvalidOperationException($"no {count} consecutive free ports above {from}");
    }

    /// <summary>Running processes whose executable is the FakeEngine apphost.</summary>
    public static IEnumerable<Process> FakeEngines()
    {
        foreach (Process p in Process.GetProcessesByName("FakeEngine"))
        {
            bool ours = false;
            try { ours = string.Equals(p.MainModule?.FileName, FakeEnginePath, StringComparison.Ordinal); }
            catch { /* exited, or not ours to inspect */ }
            if (ours) yield return p;
            else p.Dispose();
        }
    }

    public void KillLeftovers()
    {
        foreach (Process p in FakeEngines())
        {
            try { p.Kill(entireProcessTree: true); p.WaitForExit(2000); } catch { /* already gone */ }
            p.Dispose();
        }
    }

    public void Dispose()
    {
        KillLeftovers();
        foreach (var (name, value) in _saved) Environment.SetEnvironmentVariable(name, value);
        _tmp.Dispose();
    }
}
