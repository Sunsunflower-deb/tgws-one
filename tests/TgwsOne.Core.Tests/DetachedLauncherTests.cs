using System.Diagnostics;
using System.Net;

namespace TgwsOne.Core.Tests;

[Collection(EnvCollection.Name)]
public class DetachedLauncherTests : IDisposable
{
    private readonly TestEnv _env = new();

    public void Dispose() => _env.Dispose();

    private Process Launch(params string[] args)
        => DetachedLauncher.Start(TestEnv.FakeEnginePath, args, _env.Scratch("console.log"), AppContext.BaseDirectory);

    [Fact]
    public void Launcher_ListenMode_StartsListening()
    {
        int port = TestEnv.FreePort();
        using Process p = Launch("--host", "127.0.0.1", "--port", port.ToString());

        Assert.True(TestEnv.WaitFor(() => PortProbe.CanConnect(IPAddress.Loopback, port, 200)), "engine never listened");
        Assert.False(p.HasExited);
    }

    [Fact]
    public void Launcher_ExitMode_WritesConsoleLogAndKeepsExitCode()
    {
        _env.Set("FAKE_MODE", "exit:3");
        using Process p = Launch("--host", "127.0.0.1", "--port", "1");

        Assert.True(p.WaitForExit(10_000));
        Assert.Equal(3, p.ExitCode);
        Assert.Contains("fake fatal: boom", File.ReadAllText(_env.Scratch("console.log")));
    }

    [Fact]
    public void Launcher_ConsoleLog_IsTruncatedPerStart()
    {
        File.WriteAllText(_env.Scratch("console.log"), "old content from a previous run\n");
        _env.Set("FAKE_MODE", "exit:1");
        using Process p = Launch();
        p.WaitForExit(10_000);

        string log = File.ReadAllText(_env.Scratch("console.log"));
        Assert.DoesNotContain("old content", log);
        Assert.Contains("fake fatal", log);
    }

    [Fact]
    public void Launcher_PassesArgumentsVerbatim()
    {
        string argsFile = _env.Scratch("args.txt");
        _env.Set("FAKE_ARGS_FILE", argsFile);
        _env.Set("FAKE_MODE", "exit:0");
        string[] args = ["--secret", "s p a c e", "--dc-ip", "--odd", "quo\"te", "$HOME", "`x`", "a;b"];
        using Process p = Launch(args);

        Assert.True(p.WaitForExit(10_000));
        Assert.Equal(args, File.ReadAllLines(argsFile));
    }

    [Fact]
    public void Launcher_ProcessIsSessionLeader_OnLinux()
    {
        if (!OperatingSystem.IsLinux()) return;
        _env.Set("FAKE_MODE", "nolisten");
        using Process p = Launch();

        // sh runs setsid, which execs the engine: wait for that to have happened.
        Assert.True(TestEnv.WaitFor(() => Session(p.Id) == p.Id), $"session of {p.Id} is {Session(p.Id)}");
        Assert.Equal(p.Id, Session(p.Id));
    }

    [Fact]
    public void Launcher_StdinIsDevNull_OnLinux()
    {
        if (!OperatingSystem.IsLinux()) return;
        _env.Set("FAKE_MODE", "nolisten");
        using Process p = Launch();

        Assert.True(TestEnv.WaitFor(() => Session(p.Id) == p.Id));
        Assert.Equal("/dev/null", new FileInfo($"/proc/{p.Id}/fd/0").LinkTarget);
        Assert.Equal(_env.Scratch("console.log"), new FileInfo($"/proc/{p.Id}/fd/1").LinkTarget);
        Assert.Equal(_env.Scratch("console.log"), new FileInfo($"/proc/{p.Id}/fd/2").LinkTarget);
    }

    [Fact]
    public void Launcher_Pid_IsEngineItself()
    {
        _env.Set("FAKE_MODE", "nolisten");
        using Process p = Launch();
        var rec = new EngineRecord(p.Id, p.StartTime.ToUniversalTime(), TestEnv.FakeEnginePath, "h");

        // Alive means: this PID, this start time and this executable. If the launcher forked an intermediate
        // process, the PID from Process.Start would be that of sh/setsid and this would never become true.
        Assert.True(TestEnv.WaitFor(rec.IsAlive), "returned PID is not the engine's");
    }

    [Fact]
    public void WindowsCommandLine_QuotesLikeCommandLineToArgvW()
    {
        // Pure string logic of the Windows launcher, checked here because the launcher itself only runs on Windows.
        Assert.Equal(@"C:\app\tgws-engine.exe --host 127.0.0.1 --dc-ip",
            DetachedLauncher.BuildWindowsCommandLine(@"C:\app\tgws-engine.exe", ["--host", "127.0.0.1", "--dc-ip"]));
        Assert.Equal(@"""C:\Program Files\app\e.exe"" ""a b"" """"",
            DetachedLauncher.BuildWindowsCommandLine(@"C:\Program Files\app\e.exe", ["a b", ""]));
        // Embedded quotes are escaped; backslashes before the closing quote are doubled.
        Assert.Equal(@"e.exe ""say \""hi\"""" ""C:\my dir\\""",
            DetachedLauncher.BuildWindowsCommandLine("e.exe", ["say \"hi\"", @"C:\my dir\"]));
        // Backslashes before an embedded quote are doubled too, plus one to escape the quote.
        Assert.Equal("e.exe \"a\\\\\\\"b\"", DetachedLauncher.BuildWindowsCommandLine("e.exe", ["a\\\"b"]));
        // No space, tab or quote: no quoting, backslashes untouched (a lone trailing one included).
        Assert.Equal(@"e.exe C:\no\spaces\here dir\",
            DetachedLauncher.BuildWindowsCommandLine("e.exe", [@"C:\no\spaces\here", @"dir\"]));
    }

    private static int Session(int pid)
    {
        try
        {
            string stat = File.ReadAllText($"/proc/{pid}/stat");
            // "pid (comm) state ppid pgrp session ...": comm may hold spaces and parentheses, so cut after the last ')'.
            string[] f = stat[(stat.LastIndexOf(')') + 2)..].Split(' ');
            return int.Parse(f[3]);
        }
        catch { return -1; }
    }
}
