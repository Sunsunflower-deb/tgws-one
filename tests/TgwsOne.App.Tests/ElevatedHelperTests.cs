using System.ComponentModel;
using System.Runtime.Versioning;
using TgwsOne.Core.Zapret;
using TgwsOne.Services;

namespace TgwsOne.Tests;

public class ElevatedHelperTests
{
    private const string Exe = "/usr/lib/tgwsone/tgwsone";

    /// <summary>A pkexec that looks at what it was given while it still exists, and answers as told.</summary>
    private sealed class ScriptedRunner : ICommandRunner
    {
        public int Code { get; set; }
        public string Output { get; set; } = "";
        public List<(string File, string[] Args)> Calls { get; } = [];
        public string? TextOfTheInputFile { get; private set; }
        public UnixFileMode? ModeOfTheInputFile { get; private set; }

        public Task<(int Code, string Output)> RunAsync(string file, IReadOnlyList<string> args, string? stdin = null, CancellationToken ct = default)
        {
            Calls.Add((file, [.. args]));
            if (args.Contains("save-strategy"))
            {
                string input = args[^1];
                TextOfTheInputFile = File.ReadAllText(input);
                if (!OperatingSystem.IsWindows()) ModeOfTheInputFile = File.GetUnixFileMode(input);
            }
            return Task.FromResult((Code, Output));
        }
    }

    private static ElevatedHelper Helper(ScriptedRunner runner, TempDir tmp, string? pkexec = "/usr/bin/pkexec", string exe = Exe, string[]? leading = null,
                                         Func<string, IReadOnlyList<string>, Task<int>>? runas = null)
        => new(runner, exe, leading ?? [], () => pkexec, runas, tmp.Path);

    private static string[] TempEntries(TempDir tmp) => [.. Directory.GetFileSystemEntries(tmp.Path)];

    // ----- Linux: pkexec -----

    [Fact]
    public async Task Save_RunsTheAdminCommandThroughPkexec_WithTheTextInAFile()
    {
        using var tmp = new TempDir();
        var runner = new ScriptedRunner { Output = "Стратегия «my-isp» сохранена.\n" };

        AdminOutcome outcome = await Helper(runner, tmp).SaveStrategyAsync("my-isp", "--lua-desync=multisplit:pos=1\n", TestContext.Current.CancellationToken);

        Assert.True(outcome.Ok);
        Assert.Equal("Стратегия «my-isp» сохранена.", outcome.Message);
        Assert.Null(outcome.FallbackCommand);
        (string file, string[] args) = Assert.Single(runner.Calls);
        Assert.Equal("/usr/bin/pkexec", file);
        Assert.Equal([Exe, "--admin", "save-strategy", "my-isp"], args[..4]);
        Assert.EndsWith("my-isp.args", args[4]);
        Assert.Equal("--lua-desync=multisplit:pos=1\n", runner.TextOfTheInputFile);
    }

    [Fact]
    public async Task TheTextFile_CanBeReadByItsAuthorOnly_AndIsRemovedWhenDone()
    {
        if (!OperatingSystem.IsLinux()) return;
        using var tmp = new TempDir();
        var runner = new ScriptedRunner();

        await Helper(runner, tmp).SaveStrategyAsync("x", "--lua-desync=multisplit:pos=1\n", TestContext.Current.CancellationToken);

        Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, runner.ModeOfTheInputFile!.Value & (UnixFileMode)0x1FF);
        Assert.Empty(TempEntries(tmp));   // nothing left behind
    }

    [Fact]
    public async Task ACommandThatRefuses_IsAFailureWithItsOwnMessage_NotAFallback()
    {
        using var tmp = new TempDir();
        var runner = new ScriptedRunner { Code = 1, Output = "недопустимый параметр в стратегии: --debug\n" };

        AdminOutcome outcome = await Helper(runner, tmp).SaveStrategyAsync("x", "--debug=@/tmp/x", TestContext.Current.CancellationToken);

        Assert.False(outcome.Ok);
        Assert.Equal("недопустимый параметр в стратегии: --debug", outcome.Message);
        Assert.Null(outcome.FallbackCommand);
        Assert.Empty(TempEntries(tmp));
    }

    [Theory]
    [InlineData(126)]   // the user is not authorized
    [InlineData(127)]   // the dialog was dismissed, or there is no agent to show one
    [InlineData(-1)]    // pkexec could not even be started
    public async Task WhenElevationItselfFails_TheSudoCommandIsOffered_AndTheTextIsKept(int code)
    {
        using var tmp = new TempDir();
        var runner = new ScriptedRunner { Code = code };

        AdminOutcome outcome = await Helper(runner, tmp).SaveStrategyAsync("my-isp", "--lua-desync=multisplit:pos=1\n", TestContext.Current.CancellationToken);

        Assert.False(outcome.Ok);
        Assert.Contains("Не удалось запросить права администратора", outcome.Message);
        string file = runner.Calls[0].Args[^1];
        Assert.Equal($"sudo {Exe} --admin save-strategy my-isp {file}", outcome.FallbackCommand);
        Assert.True(File.Exists(file), "the file the command names has to stay until it has been run");
        Assert.Equal("--lua-desync=multisplit:pos=1\n", File.ReadAllText(file));
    }

    [Fact]
    public async Task WithoutPkexec_NothingIsRun_AndTheSudoCommandIsOffered()
    {
        using var tmp = new TempDir();
        var runner = new ScriptedRunner();

        AdminOutcome outcome = await Helper(runner, tmp, pkexec: null).SaveStrategyAsync("x", "--lua-desync=multisplit:pos=1\n", TestContext.Current.CancellationToken);

        Assert.False(outcome.Ok);
        Assert.Contains("pkexec", outcome.Message);
        Assert.StartsWith($"sudo {Exe} --admin save-strategy x ", outcome.FallbackCommand);
        Assert.Empty(runner.Calls);
    }

    [Fact]
    public async Task Delete_RunsTheAdminCommand_WithNoFile()
    {
        using var tmp = new TempDir();
        var runner = new ScriptedRunner { Output = "Стратегия «x» удалена." };

        AdminOutcome outcome = await Helper(runner, tmp).DeleteStrategyAsync("x", TestContext.Current.CancellationToken);

        Assert.True(outcome.Ok);
        Assert.Equal("/usr/bin/pkexec", runner.Calls[0].File);
        Assert.Equal([Exe, "--admin", "delete-strategy", "x"], runner.Calls[0].Args);
        Assert.Empty(TempEntries(tmp));
    }

    [Fact]
    public async Task Delete_WithoutPkexec_OffersTheSudoCommand()
    {
        using var tmp = new TempDir();

        AdminOutcome outcome = await Helper(new ScriptedRunner(), tmp, pkexec: null).DeleteStrategyAsync("x", TestContext.Current.CancellationToken);

        Assert.Equal($"sudo {Exe} --admin delete-strategy x", outcome.FallbackCommand);
    }

    [Fact]
    public async Task ADevelopmentRun_ElevatesTheDotnetHostWithTheAssembly()
    {
        using var tmp = new TempDir();
        var runner = new ScriptedRunner { Code = 127 };

        AdminOutcome outcome = await Helper(runner, tmp, exe: "/usr/bin/dotnet", leading: ["/work/TgwsOne.dll"]).DeleteStrategyAsync("x", TestContext.Current.CancellationToken);

        Assert.Equal(["/usr/bin/dotnet", "/work/TgwsOne.dll", "--admin", "delete-strategy", "x"], runner.Calls[0].Args);
        Assert.Equal("sudo /usr/bin/dotnet /work/TgwsOne.dll --admin delete-strategy x", outcome.FallbackCommand);
    }

    [Theory]
    [InlineData("/usr/lib/tgwsone/tgwsone", "/usr/lib/tgwsone/tgwsone")]
    [InlineData("my-isp", "my-isp")]
    [InlineData("/opt/Tgws One/tgwsone", "'/opt/Tgws One/tgwsone'")]
    [InlineData("it's", "'it'\\''s'")]
    [InlineData("a;b", "'a;b'")]
    [InlineData("$HOME/x", "'$HOME/x'")]
    [InlineData("", "''")]
    public void ShellQuote_KeepsAPathWithSpacesOrQuotesInOnePiece(string input, string expected)
    {
        Assert.Equal(expected, ElevatedHelper.ShellQuote(input));
    }

    [Fact]
    public async Task ThePathOfTheSudoCommand_IsQuoted_WhenItHasASpace()
    {
        using var tmp = new TempDir();
        var runner = new ScriptedRunner { Code = 127 };

        AdminOutcome outcome = await Helper(runner, tmp, exe: "/opt/Tgws One/tgwsone").DeleteStrategyAsync("x", TestContext.Current.CancellationToken);

        Assert.Equal("sudo '/opt/Tgws One/tgwsone' --admin delete-strategy x", outcome.FallbackCommand);
    }

    // ----- Windows: runas (the real UAC prompt is exercised on Windows in SP5; here only what surrounds it) -----

    [Fact]
    public async Task Windows_TheMessageComesBackInAFile_WhichIsRemoved()
    {
        using var tmp = new TempDir();
        string? resultPath = null;
        var args = new List<string>();
        Task<int> Runas(string exe, IReadOnlyList<string> a)
        {
            args.AddRange(a);
            resultPath = a[^1];
            File.WriteAllText(resultPath, "Стратегия «x» сохранена.\n");
            return Task.FromResult(0);
        }

        AdminOutcome outcome = await Helper(new ScriptedRunner(), tmp, runas: Runas).SaveStrategyAsync("x", "--lua-desync=multisplit:pos=1\n", TestContext.Current.CancellationToken);

        Assert.True(outcome.Ok);
        Assert.Equal("Стратегия «x» сохранена.", outcome.Message);
        Assert.Equal(["--admin", "save-strategy", "x"], args[..3]);
        Assert.Equal("--out", args[^2]);
        Assert.False(File.Exists(resultPath));
    }

    [Fact]
    public async Task Windows_ARefusal_IsAFailureWithTheMessageFromTheFile()
    {
        using var tmp = new TempDir();
        Task<int> Runas(string exe, IReadOnlyList<string> a)
        {
            File.WriteAllText(a[^1], "недопустимое имя «X»\n");
            return Task.FromResult(1);
        }

        AdminOutcome outcome = await Helper(new ScriptedRunner(), tmp, runas: Runas).DeleteStrategyAsync("X", TestContext.Current.CancellationToken);

        Assert.False(outcome.Ok);
        Assert.Equal("недопустимое имя «X»", outcome.Message);
        Assert.Null(outcome.FallbackCommand);
    }

    [Fact]
    public async Task Windows_ACommandThatLeftNoMessage_SaysItsExitCode()
    {
        using var tmp = new TempDir();

        AdminOutcome outcome = await Helper(new ScriptedRunner(), tmp, runas: (_, _) => Task.FromResult(5)).DeleteStrategyAsync("x", TestContext.Current.CancellationToken);

        Assert.Equal((false, "команда завершилась с кодом 5"), (outcome.Ok, outcome.Message));
    }

    [Fact]
    public async Task Windows_CancellingTheUacPrompt_IsSaidSo()
    {
        using var tmp = new TempDir();
        Task<int> Runas(string exe, IReadOnlyList<string> a) => throw new Win32Exception(1223, "The operation was canceled by the user");

        AdminOutcome outcome = await Helper(new ScriptedRunner(), tmp, runas: Runas).DeleteStrategyAsync("x", TestContext.Current.CancellationToken);

        Assert.Equal((false, "Запрос прав администратора отменён."), (outcome.Ok, outcome.Message));
        Assert.Null(outcome.FallbackCommand);
    }

    [Fact]
    public void TheHelperOfThisMachine_CanBeMade()
    {
        // Smoke test: it finds its own executable and, under the dotnet host, the assembly that is running.
        Assert.NotNull(ElevatedHelper.ForThisMachine());
    }

    // ----- what is elevated -----

    [Fact]
    public void TheRealExecutable_IsElevatedAsItIs()
    {
        Assert.Empty(ElevatedHelper.LeadingArgsFor("/usr/lib/tgwsone/tgwsone"));
        Assert.Empty(ElevatedHelper.LeadingArgsFor(@"C:\Program Files\TgwsOne\TgwsOne.exe"));
    }

    [Fact]
    public void ARunThroughTheDotnetHost_ElevatesTheDll_NotDotnetAlone()
    {
        // Found from the base directory and the assembly's name: Assembly.Location is empty in a single-file app, and the
        // build refuses code that asks it (IL3000), which is how this was found.
        string[] leading = ElevatedHelper.LeadingArgsFor("/usr/bin/dotnet");

        string dll = Assert.Single(leading);
        Assert.True(Path.IsPathRooted(dll));
        Assert.EndsWith(".dll", dll);
        Assert.True(File.Exists(dll), $"{dll} is not the application's dll");
        Assert.Equal(typeof(ElevatedHelper).Assembly.GetName().Name, Path.GetFileNameWithoutExtension(dll));
    }
}

