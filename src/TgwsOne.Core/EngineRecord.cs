using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;

namespace TgwsOne.Core;

/// <summary>
/// The engine process we started, as written to <c>engine.pid</c>. A running process is "ours" only if its PID,
/// start time and executable all match: a bare PID can be reused, and scanning by process name (as 1.x did)
/// would adopt or kill someone else's process.
/// </summary>
public sealed record EngineRecord(int Pid, DateTime StartTimeUtc, string ExePath, string ArgvHash)
{
    /// <summary>The record, or null if the file is missing or malformed.</summary>
    public static EngineRecord? Read(string pidFile)
    {
        try
        {
            string[] lines = File.ReadAllLines(pidFile);
            if (lines.Length < 4) return null;
            if (!int.TryParse(lines[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out int pid)) return null;
            if (!long.TryParse(lines[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out long ticks)
                || ticks < DateTime.MinValue.Ticks || ticks > DateTime.MaxValue.Ticks) return null;
            if (lines[2].Length == 0 || lines[3].Length == 0) return null;
            return new EngineRecord(pid, new DateTime(ticks, DateTimeKind.Utc), lines[2], lines[3]);
        }
        catch
        {
            return null;   // missing or unreadable
        }
    }

    /// <summary>One value per line: PID, start time (UTC ticks), executable path, argv hash.</summary>
    public void Write(string pidFile)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(pidFile)!);
        string tmp = $"{pidFile}.{Environment.ProcessId}.tmp";
        File.WriteAllLines(tmp,
        [
            Pid.ToString(CultureInfo.InvariantCulture),
            StartTimeUtc.Ticks.ToString(CultureInfo.InvariantCulture),
            ExePath,
            ArgvHash,
        ]);
        File.Move(tmp, pidFile, overwrite: true);
    }

    public static void Delete(string pidFile)
    {
        try { File.Delete(pidFile); }
        catch (IOException) { /* in use: the next start or stop will retry */ }
        catch (UnauthorizedAccessException) { }
    }

    /// <summary>
    /// The process exists, started within 2 s of the recorded time, and runs the recorded executable.
    /// Anything unreadable counts as "not ours": foreign processes are never touched.
    /// </summary>
    public bool IsAlive()
    {
        if (Pid <= 0) return false;
        try
        {
            using Process p = Process.GetProcessById(Pid);
            if (p.HasExited) return false;
            if (Math.Abs((p.StartTime.ToUniversalTime() - StartTimeUtc).TotalSeconds) >= 2) return false;
            string? exe = p.MainModule?.FileName;
            return exe != null && SamePath(exe, ExePath);
        }
        catch
        {
            return false;
        }
    }

    private static bool SamePath(string actual, string expected)
    {
        // A running binary that was replaced on disk (an upgrade) reads "<path> (deleted)" on Linux.
        const string deleted = " (deleted)";
        if (actual.EndsWith(deleted, StringComparison.Ordinal)) actual = actual[..^deleted.Length];

        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        return string.Equals(Canonical(actual), Canonical(expected), comparison);
    }

    // The kernel reports the fully resolved path of a running executable, so resolve ours the same way,
    // including symlinked directories (a home on /var/home, an install behind a link).
    private static string Canonical(string path)
    {
        string full = Path.GetFullPath(path);
        if (OperatingSystem.IsWindows()) return full;
        try
        {
            IntPtr resolved = RealPath(full, IntPtr.Zero);
            if (resolved != IntPtr.Zero)
            {
                try { return Marshal.PtrToStringUTF8(resolved) ?? full; }
                finally { Free(resolved); }
            }
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException) { /* fall back below */ }
        return full;
    }

    [DllImport("libc", EntryPoint = "realpath", SetLastError = true)]
    private static extern IntPtr RealPath([MarshalAs(UnmanagedType.LPUTF8Str)] string path, IntPtr resolved);

    [DllImport("libc", EntryPoint = "free")]
    private static extern void Free(IntPtr ptr);
}
