using System.Diagnostics;
using TgwsOne.Core.Zapret;

namespace TgwsOne.Core.Tests.Zapret;

/// <summary>The real executable's <c>--admin</c> switch: that <c>Program</c> routes to it, with the exit codes and messages a pipe or UAC helper relies on.</summary>
public class AdminCliProcessTests
{
    // pkexec tells the helper who asked for it in PKEXEC_UID; here that is whoever runs the tests, who also owns the files.
    private static (int Code, string Out, string Err) Run(string serviceRoot, params string[] args) => Run(serviceRoot, true, args);

    private static (int Code, string Out, string Err) Run(string serviceRoot, bool underPkexec, params string[] args)
    {
        ProcessStartInfo psi = TgwsOneProcess.StartInfo(["--admin", .. args]);
        psi.Environment["TGWS_SERVICE_ROOT"] = serviceRoot;
        psi.Environment.Remove("SUDO_UID");
        if (underPkexec) psi.Environment["PKEXEC_UID"] = LibC.GetEffectiveUserId().ToString();
        else psi.Environment.Remove("PKEXEC_UID");
        using Process p = Process.Start(psi)!;
        Task<string> err = p.StandardError.ReadToEndAsync();
        string o = p.StandardOutput.ReadToEnd();
        Assert.True(p.WaitForExit(30_000), "--admin did not exit");
        return (p.ExitCode, o.Trim(), err.GetAwaiter().GetResult().Trim());
    }

    [Fact]
    public void SaveThenDelete_WorkThroughTheRealExecutable()
    {
        using var tmp = new TempDir();
        string root = tmp.Combine("service");
        string input = tmp.Combine("in.args");
        File.WriteAllText(input, "--lua-desync=multisplit:pos=1\n");
        ServicePaths paths = ServicePaths.For(OsKind.Linux, n => n == "TGWS_SERVICE_ROOT" ? root : null);

        (int saved, string savedOut, _) = Run(root, "save-strategy", "my-isp", input);

        Assert.Equal((0, "Стратегия «my-isp» сохранена."), (saved, savedOut));
        Assert.Equal("--lua-desync=multisplit:pos=1\n", File.ReadAllText(Path.Combine(paths.StrategiesDir, "my-isp.args")));

        (int deleted, string deletedOut, _) = Run(root, "delete-strategy", "my-isp");

        Assert.Equal((0, "Стратегия «my-isp» удалена."), (deleted, deletedOut));
        Assert.False(File.Exists(Path.Combine(paths.StrategiesDir, "my-isp.args")));
    }

    [Fact]
    public void ARefusal_IsExitOne_WithTheMessageOnStderr_AndNothingWritten()
    {
        using var tmp = new TempDir();
        string root = tmp.Combine("service");
        string input = tmp.Combine("in.args");
        File.WriteAllText(input, "--debug=@/tmp/leak\n--lua-desync=multisplit:pos=1\n");

        (int code, string stdout, string stderr) = Run(root, "save-strategy", "x", input);

        Assert.Equal(1, code);
        Assert.Equal("", stdout);
        Assert.StartsWith("недопустимый параметр", stderr);
        Assert.False(Directory.Exists(Path.Combine(root, "strategies")));
    }

    [Fact]
    public void SaveWithoutPkexecOrSudo_IsRefused_AsAnUnknownCaller()
    {
        using var tmp = new TempDir();
        string root = tmp.Combine("service");
        string input = tmp.Combine("in.args");
        File.WriteAllText(input, "--lua-desync=multisplit:pos=1\n");

        (int code, string stdout, string stderr) = Run(root, underPkexec: false, "save-strategy", "x", input);

        Assert.Equal(1, code);
        Assert.Equal("", stdout);
        Assert.Contains("pkexec или sudo", stderr);
        Assert.False(Directory.Exists(Path.Combine(root, "strategies")));
    }

    [Fact]
    public void ABadCall_IsUsage_Exit64()
    {
        using var tmp = new TempDir();

        (int code, _, string stderr) = Run(tmp.Path, "frobnicate");

        Assert.Equal(64, code);
        Assert.Contains("--admin", stderr);
    }

    [Fact]
    public void TheOutFile_GetsTheMessage_AsAfterAUacPrompt()
    {
        using var tmp = new TempDir();
        string result = tmp.Combine("result.txt");

        Run(tmp.Combine("service"), "delete-strategy", "nothing", "--out", result);

        Assert.Equal("Стратегии «nothing» нет: удалять нечего.\n", File.ReadAllText(result));
    }

    [Fact]
    public void TheUsage_MentionsTheSwitch()
    {
        ProcessStartInfo psi = TgwsOneProcess.StartInfo("--help");
        using Process p = Process.Start(psi)!;
        string o = p.StandardOutput.ReadToEnd();
        p.WaitForExit();

        Assert.Contains("--admin", o);
    }
}
