using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;

namespace TgwsOne.Core;

/// <summary>
/// The engine process we started, as written to <c>engine.pid</c>. A running process is "ours" only if its PID,
/// start stamp and executable all match: a bare PID can be reused, and scanning by process name (as 1.x did)
/// would adopt or kill someone else's process.
/// </summary>
/// <param name="StartStamp">
/// When the process started, as <see cref="CurrentStamp"/> reports it. On Linux
/// <c>proc:&lt;boot id&gt;:&lt;start ticks&gt;</c>, from the kernel, compared exactly. Elsewhere
/// <c>time:&lt;UTC ticks&gt;</c>, the absolute start time, compared within one second.
/// </param>
public sealed partial record EngineRecord(int Pid, string StartStamp, string ExePath, string ArgvHash)
{
    [GeneratedRegex(@"^(proc:[0-9a-fA-F-]+:[0-9]+|time:[0-9]+)$", RegexOptions.CultureInvariant)]
    private static partial Regex ValidStamp();

    /// <summary>The record of a process that was just started; throws if it has already exited.</summary>
    public static EngineRecord ForProcess(Process process, string exePath, string argvHash)
    {
        string stamp = CurrentStamp(process.Id)
            ?? throw new InvalidOperationException($"process {process.Id} has already exited");
        return new EngineRecord(process.Id, stamp, exePath, argvHash);
    }

    /// <summary>The record, or null if the file is missing or malformed (a record we cannot parse is not ours).</summary>
    public static EngineRecord? Read(string pidFile)
    {
        try
        {
            string[] lines = File.ReadAllLines(pidFile);
            if (lines.Length < 4) return null;
            if (!int.TryParse(lines[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out int pid)) return null;
            if (!ValidStamp().IsMatch(lines[1])) return null;
            if (lines[2].Length == 0 || lines[3].Length == 0) return null;
            return new EngineRecord(pid, lines[1], lines[2], lines[3]);
        }
        catch
        {
            return null;   // missing or unreadable
        }
    }

    /// <summary>One value per line: PID, start stamp, executable path, argv hash.</summary>
    public void Write(string pidFile)
    {
        PrivateDirectory.Ensure(Path.GetDirectoryName(pidFile)!);
        string tmp = $"{pidFile}.{Environment.ProcessId}.tmp";
        File.WriteAllLines(tmp, [Pid.ToString(CultureInfo.InvariantCulture), StartStamp, ExePath, ArgvHash]);
        File.Move(tmp, pidFile, overwrite: true);
    }

    public static void Delete(string pidFile)
    {
        try { File.Delete(pidFile); }
        catch (IOException) { /* in use: the next start or stop will retry */ }
        catch (UnauthorizedAccessException) { }
    }

    /// <summary>
    /// The process exists, has the recorded start stamp, and runs the recorded executable.
    /// Anything unreadable counts as "not ours": foreign processes are never touched.
    /// </summary>
    public bool IsAlive()
    {
        if (Pid <= 0) return false;
        if (CurrentStamp(Pid) is not { } now || !SameStamp(StartStamp, now)) return false;
        try
        {
            using Process p = Process.GetProcessById(Pid);
            if (p.HasExited) return false;
            string? exe = p.MainModule?.FileName;
            return exe != null && SamePath(exe, ExePath);
        }
        catch
        {
            return false;
        }
    }

    /// <summary>The start stamp of a running process, or null if there is no such process or it cannot be read.</summary>
    public static string? CurrentStamp(int pid)
    {
        try
        {
            if (OperatingSystem.IsLinux())
            {
                // The kernel's own record of the start, in clock ticks since boot: exact, and not affected by
                // wall-clock steps (NTP, a changed date). Ticks only mean something within one boot, and PIDs
                // restart at boot, so the boot id is part of the stamp.
                string stat = File.ReadAllText($"/proc/{pid}/stat");
                int comm = stat.LastIndexOf(')');   // "pid (comm) state ..." and comm may hold spaces or ')'
                if (comm < 0) return null;
                string[] fields = stat[(comm + 2)..].Split(' ');   // fields[0] is field 3 (state)
                if (fields.Length <= 19) return null;
                string bootId = File.ReadAllText("/proc/sys/kernel/random/boot_id").Trim();
                return $"proc:{bootId}:{fields[19]}";   // field 22: starttime
            }

            // Windows: GetProcessTimes, an absolute time that does not move with the clock either.
            using Process p = Process.GetProcessById(pid);
            return "time:" + p.StartTime.ToUniversalTime().Ticks.ToString(CultureInfo.InvariantCulture);
        }
        catch
        {
            return null;
        }
    }

    internal static bool SameStamp(string recorded, string current)
    {
        const string proc = "proc:", time = "time:";
        if (recorded.StartsWith(proc, StringComparison.Ordinal) && current.StartsWith(proc, StringComparison.Ordinal))
            return string.Equals(recorded, current, StringComparison.Ordinal);

        if (recorded.StartsWith(time, StringComparison.Ordinal) && current.StartsWith(time, StringComparison.Ordinal)
            && long.TryParse(recorded[time.Length..], NumberStyles.None, CultureInfo.InvariantCulture, out long a)
            && long.TryParse(current[time.Length..], NumberStyles.None, CultureInfo.InvariantCulture, out long b))
            return Math.Abs(a - b) < TimeSpan.TicksPerSecond;   // under one second apart

        return false;
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
