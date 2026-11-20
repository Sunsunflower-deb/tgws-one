using System.Globalization;
using System.Runtime.Versioning;
using System.Text;
using Microsoft.Win32.SafeHandles;
using TgwsOne.Core;
using TgwsOne.Core.Zapret;

namespace TgwsOne.Services;

/// <summary>
/// What <see cref="AdminCli"/> asks the machine: who started it and what the files it was handed are. Linux only (Windows asks
/// nothing of this). A record of functions so that a test can say "the file belongs to somebody else" without a second user.
/// </summary>
internal sealed record AdminHost(Func<string, string?> GetEnv, Func<string, UnixFileMeta?> Lstat, Func<SafeFileHandle, UnixFileMeta?> Fstat)
{
    public static AdminHost Real { get; } = new(Environment.GetEnvironmentVariable, UnixFiles.Lstat, UnixFiles.Fstat);

    /// <summary>
    /// The user who asked for the elevation: <c>PKEXEC_UID</c> under pkexec, <c>SUDO_UID</c> under sudo. null when neither is set
    /// (the process was not started through either) or the one that is set is not a number.
    /// </summary>
    public uint? Invoker()
    {
        string? value = GetEnv("PKEXEC_UID");
        if (string.IsNullOrEmpty(value)) value = GetEnv("SUDO_UID");
        return uint.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out uint uid) ? uid : null;
    }
}

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
    /// <param name="host">Who started the process and what its files are; the real one unless a test says otherwise.</param>
    public static int Run(string[] args, ServicePaths paths, OsKind os, TextWriter stdout, TextWriter stderr, AdminHost? host = null)
    {
        string? outFile = null;
        if (args.Length >= 2 && args[^2] == "--out")
        {
            outFile = args[^1];
            args = args[..^2];
        }

        (int code, string message) = args switch
        {
            ["save-strategy", var name, var file] => Save(name, file, paths, os, host ?? AdminHost.Real),
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

    private static (int Code, string Message) Save(string name, string file, ServicePaths paths, OsKind os, AdminHost host)
    {
        if (!CustomStrategies.IsValidName(name))
            return (ExitRefused, $"недопустимое имя «{name}»: латиница в нижнем регистре, цифры и дефис, до 32 символов");

        // Linux: the helper runs as root, so it reads only a file the person who started it owns (pkexec / sudo say who that is).
        // Windows: the UAC prompt is the boundary, and the file is the user's own temp file.
        bool unix = !OperatingSystem.IsWindows() && os == OsKind.Linux;

        string text;
        try
        {
            if (!File.Exists(file)) return (ExitRefused, $"файл со стратегией не найден: {file}");

            uint invoker = 0;
            if (unix)
            {
                if (host.Invoker() is not { } uid) return (ExitRefused, "не удалось определить, кто запустил команду: запускайте её через pkexec или sudo");
                invoker = uid;
                string? refused = host.Lstat(file) is { } onDisk ? SourceError(onDisk, file, invoker) : $"не удалось проверить файл со стратегией: {file}";
                if (refused != null) return (ExitRefused, refused);
            }

            // The checks above are about the path, which can be swapped before the file is opened; what is read is the file that was
            // opened, and it is looked at again by its handle.
            using SafeFileHandle handle = File.OpenHandle(file, FileMode.Open, FileAccess.Read, FileShare.Read);
            if (unix)
            {
                string? refused = host.Fstat(handle) is { } opened ? SourceError(opened, file, invoker) : $"не удалось проверить файл со стратегией: {file}";
                if (refused != null) return (ExitRefused, refused);
            }

            if (RandomAccess.GetLength(handle) > MaxStrategyBytes) return (ExitRefused, "файл слишком большой для стратегии");
            using var reader = new StreamReader(new FileStream(handle, FileAccess.Read), new UTF8Encoding(false, throwOnInvalidBytes: true));
            text = reader.ReadToEnd();
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

    /// <summary>Null when the file may be read: an ordinary file (not a link, not a device or a pipe) that belongs to <paramref name="invoker"/>.</summary>
    private static string? SourceError(UnixFileMeta meta, string file, uint invoker)
    {
        if (meta.FileType == UnixFileMeta.Symlink) return $"{file} — символическая ссылка: укажите сам файл со стратегией";
        if (meta.FileType != UnixFileMeta.Regular) return $"{file} — не обычный файл";
        if (meta.OwnerUid != invoker)
            return $"файл {file} принадлежит другому пользователю (uid {meta.OwnerUid}): стратегию можно сохранить только из своего файла";
        return null;
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
