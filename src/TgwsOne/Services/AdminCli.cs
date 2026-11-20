using System.Runtime.Versioning;
using System.Text;
using TgwsOne.Core;
using TgwsOne.Core.Zapret;

namespace TgwsOne.Services;

/// <summary>
/// <c>tgwsone --admin save-strategy &lt;name&gt; &lt;file&gt;</c> and <c>--admin delete-strategy &lt;name&gt;</c>: the only part of the app that
/// is ever run with administrator rights (the UI starts it through <see cref="ElevatedHelper"/>). What it writes is run as root /
/// SYSTEM by the bypass service, so it trusts nothing it is handed: the name and the text are checked again here exactly as the
/// service will check them, and a strategy is refused if it could not work. Prints a Russian message; exit 0 done, 1 refused.
/// </summary>
internal static class AdminCli
{
    public const int ExitOk = 0;
    public const int ExitRefused = 1;
    public const int ExitUsage = 64;   // EX_USAGE, as in Program

    /// <summary>A strategy is a handful of lines; anything bigger is not one.</summary>
    public const long MaxStrategyBytes = 64 * 1024;

    public const string Usage = "служебная команда: tgwsone --admin save-strategy <имя> <файл> | delete-strategy <имя> [--out <файл>]";

    private const UnixFileMode PublicRead = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead | UnixFileMode.OtherRead;   // 0644

    /// <param name="args">What follows <c>--admin</c>. A trailing <c>--out &lt;file&gt;</c> also gets the message (when there is no console to read it from, as after a UAC prompt).</param>
    public static int Run(string[] args, ServicePaths paths, OsKind os, TextWriter stdout, TextWriter stderr)
    {
        string? outFile = null;
        if (args.Length >= 2 && args[^2] == "--out")
        {
            outFile = args[^1];
            args = args[..^2];
        }

        (int code, string message) = args switch
        {
            ["save-strategy", var name, var file] => Save(name, file, paths, os),
            ["delete-strategy", var name] => Delete(name, paths),
            _ => (ExitUsage, Usage),
        };

        (code == ExitOk ? stdout : stderr).WriteLine(message);
        if (outFile != null)
        {
            try { File.WriteAllText(outFile, message + "\n", new UTF8Encoding(false)); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { /* the console message is the one that counts */ }
        }
        return code;
    }

    private static (int Code, string Message) Save(string name, string file, ServicePaths paths, OsKind os)
    {
        if (!CustomStrategies.IsValidName(name))
            return (ExitRefused, $"недопустимое имя «{name}»: латиница в нижнем регистре, цифры и дефис, до 32 символов");

        string text;
        try
        {
            var info = new FileInfo(file);
            if (!info.Exists) return (ExitRefused, $"файл со стратегией не найден: {file}");
            if (info.Length > MaxStrategyBytes) return (ExitRefused, "файл слишком большой для стратегии");
            text = File.ReadAllText(file, new UTF8Encoding(false, throwOnInvalidBytes: true));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or DecoderFallbackException)
        {
            return (ExitRefused, $"не удалось прочитать файл со стратегией: {ex.Message}");
        }

        (DesyncPart? part, string? error) = CustomStrategies.Parse(text);
        if (part is null) return (ExitRefused, error!);
        if (!OperatingSystem.IsWindows() && os == OsKind.Linux && ReferencedFilesError(part.Args) is { } unreachable)
            return (ExitRefused, unreachable);

        string target = Path.Combine(paths.StrategiesDir, name + ".args");
        try
        {
            WriteAtomically(paths, target, text.TrimEnd() + "\n");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return (ExitRefused, $"не удалось записать стратегию: {ex.Message}");
        }
        return (ExitOk, $"Стратегия «{name}» сохранена.");
    }

    private static (int Code, string Message) Delete(string name, ServicePaths paths)
    {
        if (!CustomStrategies.IsValidName(name)) return (ExitRefused, $"недопустимое имя «{name}»");

        string target = Path.Combine(paths.StrategiesDir, name + ".args");
        try
        {
            if (!File.Exists(target)) return (ExitOk, $"Стратегии «{name}» нет: удалять нечего.");
            File.Delete(target);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return (ExitRefused, $"не удалось удалить стратегию: {ex.Message}");
        }
        return (ExitOk, $"Стратегия «{name}» удалена.");
    }

    // A temp file in the same folder, then a rename: the service never reads a half-written strategy, and a failed write leaves
    // the old one alone. The folder is opened for everybody to read (nfqws2 gets there after dropping root); nobody but the
    // administrator can write to it.
    private static void WriteAtomically(ServicePaths paths, string target, string text)
    {
        PublicDirectory.Ensure(paths.StrategiesDir);
        string temp = $"{target}.{Environment.ProcessId}.tmp";
        File.WriteAllText(temp, text, new UTF8Encoding(false));
        try
        {
            if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(temp, PublicRead);
            File.Move(temp, target, overwrite: true);
        }
        catch
        {
            try { File.Delete(temp); } catch { /* best effort */ }
            throw;
        }
    }

    /// <summary>
    /// Linux: nfqws2 drops root privileges (to uid 0x7FFFFFFF) after it starts and then opens the files a strategy names
    /// (<c>--blob=name:@file</c>, <c>--lua-init=@file</c>), so each must exist, be readable by others, and sit in folders
    /// others can enter. A strategy that fails this would pass the dry run (which runs as root) and then break at runtime.
    /// </summary>
    [UnsupportedOSPlatform("windows")]
    internal static string? ReferencedFilesError(IReadOnlyList<string> args)
    {
        foreach (string arg in args)
        {
            if (!(arg.StartsWith("--blob=", StringComparison.Ordinal) || arg.StartsWith("--lua-init=", StringComparison.Ordinal))) continue;
            int at = arg.IndexOf('@');
            if (at < 0) continue;   // a literal blob (0x…), no file

            string path = arg[(at + 1)..];
            if (!Path.IsPathRooted(path)) return $"укажите полный путь к файлу в «{arg}»: относительный путь зависит от того, откуда запущен процесс";
            if (!File.Exists(path)) return $"файл из «{arg}» не найден: {path}";
            if ((File.GetUnixFileMode(path) & UnixFileMode.OtherRead) == 0)
                return $"файл {path} недоступен для чтения всем: после запуска nfqws2 теряет права root и не сможет его открыть (chmod o+r)";

            for (string? dir = Path.GetDirectoryName(path); !string.IsNullOrEmpty(dir); dir = Path.GetDirectoryName(dir))
                if ((File.GetUnixFileMode(dir) & UnixFileMode.OtherExecute) == 0)
                    return $"в папку {dir} нельзя войти всем: nfqws2 после запуска теряет права root и не доберётся до {path} (chmod o+x)";
        }
        return null;
    }
}
