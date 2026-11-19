using System.Diagnostics;
using System.Text;

namespace TgwsOne.Core;

/// <summary>
/// Starts a program detached from the caller: it keeps running after the caller exits and after the terminal
/// it was started from closes. Its stdout and stderr go to a file, never to a pipe the caller owns.
/// </summary>
public static partial class DetachedLauncher
{
    /// <summary>
    /// Linux: own session (<c>setsid</c>), stdin from <c>/dev/null</c>. Windows: no console window, outside the
    /// caller's job object where allowed. Output goes to <paramref name="consoleLog"/> (truncated per start).
    /// The returned process is the program itself, not a wrapper, so its PID can be recorded.
    /// </summary>
    public static Process Start(string exe, IReadOnlyList<string> args, string consoleLog, string workDir)
    {
        string? logDir = Path.GetDirectoryName(consoleLog);
        if (!string.IsNullOrEmpty(logDir)) PrivateDirectory.Ensure(logDir);

        return OperatingSystem.IsWindows()
            ? StartWindows(exe, args, consoleLog, workDir)
            : StartUnix(exe, args, consoleLog, workDir);
    }

    /// <summary>
    /// The inverse of <c>CommandLineToArgvW</c> quoting: an argument is quoted only if it is empty or holds a
    /// space, tab, line break or quote; backslashes are doubled only where they precede a quote.
    /// </summary>
    internal static string BuildWindowsCommandLine(string exe, IReadOnlyList<string> args)
    {
        var sb = new StringBuilder();
        AppendWindowsArg(sb, exe);
        foreach (string a in args)
        {
            sb.Append(' ');
            AppendWindowsArg(sb, a);
        }
        return sb.ToString();
    }

    private static void AppendWindowsArg(StringBuilder sb, string arg)
    {
        if (arg.Length > 0 && arg.IndexOfAny([' ', '\t', '\n', '\v', '"']) < 0)
        {
            sb.Append(arg);
            return;
        }

        sb.Append('"');
        for (int i = 0; ; i++)
        {
            int backslashes = 0;
            while (i < arg.Length && arg[i] == '\\') { i++; backslashes++; }

            if (i == arg.Length)
            {
                sb.Append('\\', backslashes * 2);   // they now precede the closing quote
                break;
            }
            if (arg[i] == '"') sb.Append('\\', backslashes * 2 + 1).Append('"');
            else sb.Append('\\', backslashes).Append(arg[i]);
        }
        sb.Append('"');
    }
}
