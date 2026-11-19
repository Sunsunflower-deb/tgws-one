using System.Diagnostics;

namespace TgwsOne.Core.Zapret.Windows;

/// <summary>
/// 1.x ran zapret1's <c>winws.exe</c> from a SYSTEM scheduled task (and, in "all users" mode, left a flag file and a task
/// definition in ProgramData). The service replaces all of it, so on every start it removes what 1.x left, if anything.
/// Everything here is idempotent and ignores failures: a clean machine has nothing to remove.
/// </summary>
public static class LegacyMigration
{
    public const string TaskName = "TgwsOne-Zapret";
    private const string LegacyBinSubdir = "zapret";

    /// <summary>The commands as data, so the plan can be checked without Windows: stop the task, then delete it.</summary>
    public static IReadOnlyList<(string File, string[] Args)> Plan() =>
    [
        ("schtasks.exe", ["/end", "/tn", TaskName]),
        ("schtasks.exe", ["/delete", "/f", "/tn", TaskName]),
    ];

    /// <summary>
    /// Runs the plan, kills the <c>winws.exe</c> processes that live under <c>&lt;appDir&gt;\zapret\bin\</c> (not zapret2's
    /// <c>winws2.exe</c>, and not a zapret the user runs from somewhere else), and deletes <c>all-users.flag</c> and
    /// <c>task.xml</c> from <paramref name="root"/>.
    /// </summary>
    /// <param name="processes">Running winws processes as (PID, executable path); the real ones by default.</param>
    /// <param name="kill">Ends a process by PID; the real thing by default.</param>
    public static async Task RunAsync(ICommandRunner runner, string appDir, string root,
        Func<IEnumerable<(int Pid, string Path)>>? processes = null, Action<int>? kill = null, AppLog? log = null)
    {
        foreach ((string file, string[] args) in Plan())
        {
            (int code, _) = await runner.RunAsync(file, args);
            if (code == 0 && args[0] == "/delete") log?.Write($"миграция с 1.x: задача {TaskName} удалена");
        }

        string legacyBin = OsPath.Join(OsKind.Windows, appDir, LegacyBinSubdir, "bin") + "\\";
        foreach ((int pid, string path) in (processes ?? RunningWinws)().ToList())
        {
            if (!path.StartsWith(legacyBin, StringComparison.OrdinalIgnoreCase)) continue;
            try
            {
                (kill ?? KillProcess)(pid);
                log?.Write($"миграция с 1.x: остановлен winws.exe (pid {pid})");
            }
            catch (Exception ex)
            {
                log?.Write($"миграция с 1.x: не удалось остановить pid {pid}: {ex.Message}");
            }
        }

        foreach (string name in new[] { "all-users.flag", "task.xml" })
        {
            try { File.Delete(Path.Combine(root, name)); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { log?.Write($"миграция с 1.x: не удалось удалить {name}: {ex.Message}"); }
        }
    }

    private static IEnumerable<(int Pid, string Path)> RunningWinws()
    {
        foreach (Process p in Process.GetProcessesByName("winws"))
        {
            using (p)
            {
                string? path = null;
                try { path = p.MainModule?.FileName; }
                catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException) { /* exited, or not ours to inspect */ }
                if (path != null) yield return (p.Id, path);
            }
        }
    }

    private static void KillProcess(int pid)
    {
        using Process p = Process.GetProcessById(pid);
        p.Kill(entireProcessTree: true);
    }
}
