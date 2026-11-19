using System.Diagnostics;

namespace TgwsOne.Core;

public static partial class DetachedLauncher
{
    // sh -> `exec setsid` -> exe: nothing forks along the way, so the PID Process.Start returns is the engine's.
    // (setsid forks only when its caller is a process group leader; a plain `sh -c` child is not one.)
    // The program path and arguments reach the script as positional parameters, so no quoting is involved.
    private static Process StartUnix(string exe, IReadOnlyList<string> args, string consoleLog, string workDir)
    {
        var psi = new ProcessStartInfo("/bin/sh") { UseShellExecute = false, WorkingDirectory = workDir };
        psi.ArgumentList.Add("-c");
        psi.ArgumentList.Add("exec setsid -- \"$0\" \"$@\" </dev/null >\"$TGWS_CONSOLE_LOG\" 2>&1");
        psi.ArgumentList.Add(exe);
        foreach (string a in args) psi.ArgumentList.Add(a);
        psi.Environment["TGWS_CONSOLE_LOG"] = consoleLog;
        return Process.Start(psi)!;
    }
}
