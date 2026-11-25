using System.Diagnostics;
using System.Net;

namespace TgwsOne.Core.Tests;

/// <summary>Runs the real CLI (TgwsOne.dll) as a separate process, as a user or the GUI would.</summary>
[Collection(EnvCollection.Name)]
public class CliTests : IDisposable
{
    private const string Secret = "0123456789abcdef0123456789abcdef";
    private readonly TestEnv _env = new();

    public void Dispose() => _env.Dispose();

    private AppPaths Paths => _env.Paths;

    private (int Code, string Out, string Err) RunCli(params string[] args)
    {
        ProcessStartInfo psi = TgwsOneProcess.StartInfo(args);
        foreach (var (k, v) in _env.ChildEnv) psi.Environment[k] = v;

        using Process p = Process.Start(psi)!;
        var err = p.StandardError.ReadToEndAsync();
        // Reading to the end only returns once every holder of the pipe is gone: an engine that kept our
        // stdout open would hang this call, which is exactly what "detached" has to prevent.
        string o = p.StandardOutput.ReadToEnd();
        Assert.True(p.WaitForExit(30_000), "CLI did not exit");
        return (p.ExitCode, o, err.GetAwaiter().GetResult());
    }

    private int Configure(string? secret = null)
    {
        int port = TestEnv.FreePort();
        var ini = IniFile.Load(Paths.Ini);
        ini.Set("proxy", "port", port.ToString());
        if (secret != null) ini.Set("app", "secret", secret);
        ini.Save();
        return port;
    }

    private static bool Listening(int port) => PortProbe.CanConnect(IPAddress.Loopback, port, 300);

    /// <summary>The real engine started by hand, <c>tgwsone --engine …</c>, as from a terminal. The caller ends it.</summary>
    private Process StartEngineByHand(int port)
    {
        ProcessStartInfo psi = TgwsOneProcess.StartInfo("--engine", "--host", "127.0.0.1", "--port", port.ToString(),
            "--secret", Secret, "--dc-ip", "--no-cfproxy", "--pool-size", "0", "--cfproxy-domain", "example.test");
        foreach (var (k, v) in _env.ChildEnv) psi.Environment[k] = v;
        return Process.Start(psi)!;
    }

    [Fact]
    public void EngineStartedByHand_IsOnRecord_ForStatusAndStop()
    {
        // `tgwsone --engine …` from a terminal. No controller starts it, so nothing writes engine.pid for it:
        // the engine has to put itself on record, or --status does not see it and --stop leaves it running.
        int port = Configure();
        using Process engine = StartEngineByHand(port);
        try
        {
            Assert.True(TestEnv.WaitFor(() => PortProbe.IsBusy(IPAddress.Loopback, port)), "the engine did not start listening");

            var status = RunCli("--status");
            Assert.Equal(0, status.Code);
            Assert.Contains($"running=true listening=true port={port}", status.Out);

            var start = RunCli("--start");
            Assert.Equal(0, start.Code);
            Assert.Contains($"уже запущен (порт {port})", start.Out);

            var stop = RunCli("--stop");
            Assert.Equal(0, stop.Code);
            Assert.Contains("остановлен", stop.Out);
            Assert.True(TestEnv.WaitFor(() => !Listening(port)), "port still listening after --stop");
            Assert.True(TestEnv.WaitFor(() => engine.HasExited), "the engine process outlived --stop");
        }
        finally
        {
            // Whatever the assertions found, the engine must not outlive the test.
            if (!engine.HasExited) engine.Kill(entireProcessTree: true);
        }
    }

    [Fact]
    public void EngineStartedByHand_NextToARegisteredOne_DoesNotTakeItsPlace()
    {
        // The controller has one engine on record. A second, started by hand, must not write its own record over it:
        // --stop has to keep reaching the engine the controller started.
        int port = Configure();
        Assert.Equal(0, RunCli("--start").Code);
        int handPort = TestEnv.FreePort();
        using Process hand = StartEngineByHand(handPort);
        try
        {
            Assert.True(TestEnv.WaitFor(() => PortProbe.IsBusy(IPAddress.Loopback, handPort)), "the hand-started engine did not start");

            var stop = RunCli("--stop");

            Assert.Contains("остановлен", stop.Out);
            Assert.True(TestEnv.WaitFor(() => !TestEnv.FakeEngines().Any()), "--stop did not reach the engine on record");
        }
        finally
        {
            if (!hand.HasExited) hand.Kill(entireProcessTree: true);
        }
    }

    [Fact]
    public void Cli_StartExits_EngineKeepsListening_StopFromAnotherProcess()
    {
        int port = Configure();

        var start = RunCli("--start");

        Assert.Equal(0, start.Code);
        Assert.Contains($"запущен (порт {port})", start.Out);
        // The CLI process is gone (RunCli waited for it); the engine must still be serving.
        Assert.True(Listening(port), "engine died with the CLI process");
        Assert.Single(TestEnv.FakeEngines());

        var status = RunCli("--status");
        Assert.Equal(0, status.Code);
        Assert.Contains($"running=true listening=true port={port}", status.Out);

        var again = RunCli("--start");
        Assert.Equal(0, again.Code);
        Assert.Contains($"уже запущен (порт {port})", again.Out);

        var stop = RunCli("--stop");
        Assert.Equal(0, stop.Code);
        Assert.Contains("остановлен", stop.Out);
        Assert.True(TestEnv.WaitFor(() => !Listening(port)), "port still listening after --stop");
        Assert.Empty(TestEnv.FakeEngines());

        var stopAgain = RunCli("--stop");
        Assert.Equal(0, stopAgain.Code);
        Assert.Contains("не был запущен", stopAgain.Out);
    }

    [Fact]
    public async Task Cli_TwoConcurrentStarts_ExactlyOneEngine()
    {
        int port = Configure();

        // Two separate processes racing for the same engine: the start lock makes the loser see AlreadyRunning.
        var first = Task.Run(() => RunCli("--start"));
        var second = Task.Run(() => RunCli("--start"));
        var results = await Task.WhenAll(first, second);

        Assert.All(results, r => Assert.Equal(0, r.Code));
        Assert.Single(results, r => r.Out.Contains($"уже запущен (порт {port})"));
        Assert.Single(results, r => r.Out.Contains($"запущен (порт {port})") && !r.Out.Contains("уже"));
        Assert.Single(TestEnv.FakeEngines());
        Assert.True(Listening(port));
    }

    [Fact]
    public void Cli_Start_PortBusy_ReportsSwitch()
    {
        int port = Configure();
        using var blocker = new System.Net.Sockets.TcpListener(IPAddress.Loopback, port);
        blocker.Start();

        var start = RunCli("--start");

        Assert.Equal(0, start.Code);
        Assert.Matches($@"порт {port} занят — запущен на порту \d+", start.Out);
    }

    [Fact]
    public void Cli_Status_NotRunning_Exit1()
    {
        int port = Configure();

        var status = RunCli("--status");

        Assert.Equal(1, status.Code);
        Assert.Contains($"running=false listening=false port={port}", status.Out);
    }

    [Fact]
    public void Cli_Start_EngineMissing_Exit2()
    {
        Configure();
        _env.Set("TGWS_ENGINE", Path.Combine(_env.DataDir, "no-engine"));

        var start = RunCli("--start");

        Assert.Equal(2, start.Code);
        Assert.Contains("не удалось запустить", start.Err);
        Assert.Contains("движок не найден", start.Err);
    }

    [Fact]
    public void Cli_Start_EngineExits_Exit2_ShowsItsOutput()
    {
        Configure();
        _env.Set("FAKE_MODE", "exit:5");

        var start = RunCli("--start");

        Assert.Equal(2, start.Code);
        Assert.Contains("не удалось запустить", start.Err);
        Assert.Contains("5", start.Err);
        Assert.Contains("fake fatal: boom", start.Err);
    }

    [Fact]
    public void Cli_Selftest_EngineMissing_Exit1_AndMasksSecret()
    {
        Configure(Secret);
        _env.Set("TGWS_ENGINE", Path.Combine(_env.DataDir, "no-engine"));

        var r = RunCli("--selftest");

        Assert.Equal(1, r.Code);
        Assert.Contains("engine: MISSING", r.Out);
        Assert.Contains("secret=ok", r.Out);
        Assert.Contains("RESULT: 1 problem(s)", r.Out);
        Assert.DoesNotContain(Secret, r.Out);
        Assert.Contains("--secret ********", r.Out);

        string log = File.ReadAllText(Path.Combine(Paths.LogsDir, "selftest.log"));
        Assert.Contains("RESULT: 1 problem(s)", log);
        Assert.DoesNotContain(Secret, log);
    }

    [Fact]
    public void Cli_Selftest_Healthy_Exit0()
    {
        int port = Configure(Secret);

        var r = RunCli("--selftest");

        Assert.Equal(0, r.Code);
        Assert.Contains($"engine: OK {TestEnv.FakeEnginePath}", r.Out);
        Assert.Contains($"port={port}", r.Out);
        Assert.Contains("running=false", r.Out);
        Assert.Contains($"link: tg://proxy?server=127.0.0.1&port={port}&secret=dd********", r.Out);
        Assert.Contains("RESULT: OK", r.Out);
        Assert.DoesNotContain(Secret, r.Out);
        Assert.Empty(TestEnv.FakeEngines());   // a self-test must not start anything
    }

    [Fact]
    public void Cli_Selftest_DoesNotCreateTheSecret()
    {
        // Read-only: a missing secret is reported, not repaired.
        Configure();

        var r = RunCli("--selftest");

        Assert.Contains("secret=invalid", r.Out);
        Assert.Null(IniFile.Load(Paths.Ini).Get("app", "secret"));
    }

    [Fact]
    public void Cli_Unknown_Exit64()
    {
        var r = RunCli("--bogus");

        Assert.Equal(64, r.Code);
        Assert.Contains("--start", r.Err);   // usage goes to stderr
        Assert.Equal("", r.Out);
    }

    // From SP3 on, no arguments (or only --tray) start the window; the usage text is what --help prints.
    [Theory]
    [InlineData("--help")]
    [InlineData("-h")]
    public void Cli_Help_PrintsUsage_Exit0(string flag)
    {
        var r = RunCli(flag);

        Assert.Equal(0, r.Code);
        Assert.Contains("--start", r.Out);
        Assert.Contains("--selftest", r.Out);
    }

    [Fact]
    public void Cli_Version_PrintsTheAssemblyVersion()
    {
        var r = RunCli("--version");

        Assert.Equal(0, r.Code);
        // The product version comes from Directory.Build.props: assert against it instead of a literal, so bumping the
        // version does not break this test.
        string expected = typeof(TgwsOne.Core.ProxyController).Assembly.GetName().Version!.ToString(3);
        Assert.Contains(expected, r.Out);
    }
}
