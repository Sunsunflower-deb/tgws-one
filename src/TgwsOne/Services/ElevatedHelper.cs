using System.ComponentModel;
using System.Diagnostics;
using System.Text;
using TgwsOne.Core;
using TgwsOne.Core.Zapret;

namespace TgwsOne.Services;

/// <summary>
/// How an elevated command ended. <see cref="FallbackCommand"/> is set when the elevation itself was not possible (no pkexec, no
/// polkit agent to ask): the exact command the user can run in a terminal with <c>sudo</c> instead.
/// </summary>
internal sealed record AdminOutcome(bool Ok, string Message, string? FallbackCommand = null);

/// <summary>Saves and deletes the custom strategies, which only an administrator may: the service runs them as root / SYSTEM.</summary>
internal interface IElevatedHelper
{
    Task<AdminOutcome> SaveStrategyAsync(string name, string text, CancellationToken ct = default);
    Task<AdminOutcome> DeleteStrategyAsync(string name, CancellationToken ct = default);
}

/// <summary>
/// Runs <c>tgwsone --admin …</c> with administrator rights: <c>pkexec</c> on Linux (polkit asks for the administrator's password),
/// the UAC prompt through <c>runas</c> on Windows (a standard user is asked for administrator credentials). Where elevation is not
/// possible it says what to type in a terminal instead of failing quietly.
/// </summary>
internal sealed class ElevatedHelper : IElevatedHelper
{
    // pkexec: 126 = the user is not authorized, 127 = the authentication dialog was dismissed or there is no agent to show one.
    private const int PkexecNotAuthorized = 126;
    private const int PkexecNoAgent = 127;
    private const int UacCancelled = 1223;   // ERROR_CANCELLED

    private readonly ICommandRunner _runner;
    private readonly string _exe;
    private readonly string[] _leadingArgs;
    private readonly Func<string?> _findPkexec;
    private readonly Func<string, IReadOnlyList<string>, Task<int>>? _windowsRunas;
    private readonly string _tempRoot;

    /// <param name="runner">Runs pkexec (its prompt can take a while: give it a long timeout).</param>
    /// <param name="exe">The program to run elevated; with a <paramref name="leadingArgs"/> when that is the dotnet host (a development run).</param>
    /// <param name="findPkexec">Where pkexec is, or null.</param>
    /// <param name="windowsRunas">Windows only: starts the program with the <c>runas</c> verb and returns its exit code.</param>
    /// <param name="tempRoot">Where the strategy's text is put for the elevated process to read.</param>
    public ElevatedHelper(ICommandRunner runner, string exe, string[] leadingArgs, Func<string?> findPkexec,
                          Func<string, IReadOnlyList<string>, Task<int>>? windowsRunas, string tempRoot)
    {
        _runner = runner;
        _exe = exe;
        _leadingArgs = leadingArgs;
        _findPkexec = findPkexec;
        _windowsRunas = windowsRunas;
        _tempRoot = tempRoot;
    }

    /// <summary>The helper for this machine, running the program that is running now.</summary>
    public static ElevatedHelper ForThisMachine()
    {
        string exe = Environment.ProcessPath ?? "tgwsone";
        // A development run goes through the dotnet host: elevating "dotnet" alone would run nothing of ours.
        string[] leading = Path.GetFileNameWithoutExtension(exe) == "dotnet" && System.Reflection.Assembly.GetEntryAssembly()?.Location is { Length: > 0 } dll
            ? [dll]
            : [];
        return new ElevatedHelper(new ProcessCommandRunner(TimeSpan.FromMinutes(5)), exe, leading, FindOnPath, OperatingSystem.IsWindows() ? RunWithUac : null,
                                  Path.GetTempPath());
    }

    public async Task<AdminOutcome> SaveStrategyAsync(string name, string text, CancellationToken ct = default)
    {
        // The text goes in a file only its author can read; the elevated process reads that one file and validates it again.
        string dir = Path.Combine(_tempRoot, "tgwsone-" + Guid.NewGuid().ToString("N"));
        PrivateDirectory.Ensure(dir);
        string file = Path.Combine(dir, name + ".args");
        await File.WriteAllTextAsync(file, text, new UTF8Encoding(false), ct);
        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(file, UnixFileMode.UserRead | UnixFileMode.UserWrite);

        AdminOutcome outcome = await RunAdminAsync(["save-strategy", name, file], ct);

        // With a command to run by hand the file has to stay until the user has run it.
        if (outcome.FallbackCommand == null) DeleteQuietly(dir);
        return outcome;
    }

    public Task<AdminOutcome> DeleteStrategyAsync(string name, CancellationToken ct = default)
        => RunAdminAsync(["delete-strategy", name], ct);

    private async Task<AdminOutcome> RunAdminAsync(string[] adminArgs, CancellationToken ct)
    {
        string[] full = [.. _leadingArgs, "--admin", .. adminArgs];
        return _windowsRunas != null ? await RunWithRunasAsync(full, ct) : await RunWithPkexecAsync(full, ct);
    }

    // ----- Linux -----

    private async Task<AdminOutcome> RunWithPkexecAsync(string[] args, CancellationToken ct)
    {
        string? pkexec = _findPkexec();
        if (pkexec is null)
            return new AdminOutcome(false, "Не нашёл pkexec: запросить права администратора не получится.", SudoCommand(args));

        (int code, string output) = await _runner.RunAsync(pkexec, [_exe, .. args], null, ct);
        string message = output.Trim();
        if (code == 0) return new AdminOutcome(true, message);

        // The elevation itself failed (pkexec would not start, nobody could be asked, the answer was no): not our command's refusal.
        if (code is PkexecNotAuthorized or PkexecNoAgent || code < 0)
            return new AdminOutcome(false, "Не удалось запросить права администратора: нет окна запроса пароля или доступ отклонён.", SudoCommand(args));

        return new AdminOutcome(false, message.Length > 0 ? message : $"команда завершилась с кодом {code}");
    }

    /// <summary>The same command for a terminal. Quoted for a POSIX shell, so a path with a space or a quote survives.</summary>
    private string SudoCommand(string[] args) => "sudo " + string.Join(' ', new[] { _exe }.Concat(args).Select(ShellQuote));

    internal static string ShellQuote(string s)
        => s.Length > 0 && s.All(c => char.IsAsciiLetterOrDigit(c) || c is '/' or '.' or '-' or '_' or ':' or '=' or '+')
            ? s
            : "'" + s.Replace("'", "'\\''") + "'";

    private static string? FindOnPath()
    {
        foreach (string dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            string candidate = Path.Combine(dir, "pkexec");
            if (File.Exists(candidate)) return candidate;
        }
        return null;
    }

    // ----- Windows (compiled everywhere, exercised on Windows in SP5) -----

    private async Task<AdminOutcome> RunWithRunasAsync(string[] args, CancellationToken ct)
    {
        // A process started through the shell has no console to read from: its message comes back in a file.
        string resultFile = Path.Combine(_tempRoot, "tgwsone-admin-" + Guid.NewGuid().ToString("N") + ".txt");
        try
        {
            int code = await _windowsRunas!(_exe, [.. args, "--out", resultFile]);
            string message = File.Exists(resultFile) ? (await File.ReadAllTextAsync(resultFile, ct)).Trim() : "";
            return code == 0
                ? new AdminOutcome(true, message)
                : new AdminOutcome(false, message.Length > 0 ? message : $"команда завершилась с кодом {code}");
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == UacCancelled)
        {
            return new AdminOutcome(false, "Запрос прав администратора отменён.");
        }
        finally
        {
            DeleteQuietly(resultFile);
        }
    }

    private static Task<int> RunWithUac(string exe, IReadOnlyList<string> args)
    {
        var psi = new ProcessStartInfo(exe) { UseShellExecute = true, Verb = "runas", WindowStyle = ProcessWindowStyle.Hidden };
        foreach (string a in args) psi.ArgumentList.Add(a);
        return Task.Run(() =>
        {
            using Process p = Process.Start(psi) ?? throw new Win32Exception("процесс не запустился");
            p.WaitForExit();
            return p.ExitCode;
        });
    }

    private static void DeleteQuietly(string path)
    {
        try
        {
            if (Directory.Exists(path)) Directory.Delete(path, recursive: true);
            else File.Delete(path);
        }
        catch { /* a temp file that stays is not worth failing for */ }
    }
}
