using System.Diagnostics;

namespace TgwsOne.Core.Tests;

/// <summary>A fact that runs only when TGWS_REAL_ENGINE points at a built engine (<c>make test-engine</c>).</summary>
public sealed class RealEngineFactAttribute : FactAttribute
{
    public const string Variable = "TGWS_REAL_ENGINE";

    public RealEngineFactAttribute()
    {
        if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable(Variable)))
            Skip = $"opt-in: set {Variable} to the built tgws-engine (make test-engine)";
    }
}

/// <summary>The real PyInstaller-built engine instead of FakeEngine: proves the argv we build is what it accepts.</summary>
[Collection(EnvCollection.Name)]
public class RealEngineTests : IDisposable
{
    private readonly TestEnv _env = new();
    private readonly string _engine = Environment.GetEnvironmentVariable(RealEngineFactAttribute.Variable) ?? "";

    public void Dispose()
    {
        KillEngines();
        _env.Dispose();
    }

    private ProxyController Controller()
    {
        _env.Set("TGWS_ENGINE", _engine);
        return new ProxyController(_env.Paths, new AppLog(_env.Paths.AppLogFile), TimeSpan.FromSeconds(30));
    }

    private int Configure(string? dcMap = null)
    {
        int port = TestEnv.FreePort();
        var ini = IniFile.Load(_env.Paths.Ini);
        ini.Set("proxy", "port", port.ToString());
        if (dcMap != null) ini.Set("proxy", "dcmap", dcMap);
        ini.Save();
        return port;
    }

    // Both the PyInstaller bootloader and the interpreter process it spawns run this very file.
    private IEnumerable<Process> EngineProcesses()
        => Process.GetProcessesByName(Path.GetFileNameWithoutExtension(_engine)).Where(p =>
        {
            try { return p.MainModule?.FileName is { } f && Path.GetFileName(f) == Path.GetFileName(_engine); }
            catch { return false; }
        });

    private void KillEngines()
    {
        if (_engine.Length == 0) return;
        foreach (Process p in EngineProcesses())
            try { p.Kill(entireProcessTree: true); } catch { /* gone */ }
    }

    [RealEngineFact]
    public async Task RealEngine_StartListenStatusStop()
    {
        int port = Configure();
        var c = Controller();

        StartResult r = await c.StartAsync();

        Assert.True(r.Outcome == StartOutcome.Started, $"{r.Outcome}: exit {r.ExitCode}\n{r.Output}");
        Assert.Equal(port, r.Port);
        ProxyStatus st = c.GetStatus();
        Assert.True(st.Running);
        Assert.True(st.Listening);
        Assert.False(c.RestartNeeded());
        // The engine really took our flags: it writes the log file we asked for.
        Assert.True(TestEnv.WaitFor(() => File.Exists(_env.Paths.ProxyLogFile)), "engine did not create proxy.log");

        Assert.Equal(StopOutcome.Stopped, await c.StopAsync());

        Assert.False(c.GetStatus().Running);
        Assert.True(TestEnv.WaitFor(() => !EngineProcesses().Any()), "tgws-engine processes left after stop (the PyInstaller child too)");
        Assert.True(TestEnv.WaitFor(() => !PortProbe.IsBusy(System.Net.IPAddress.Loopback, port)), "port still busy after stop");
    }

    [RealEngineFact]
    public async Task RealEngine_EmptyDcMap_StartsWithBareDcIp()
    {
        // A bare --dc-ip is how an empty DC list reaches the engine; it must parse, not fall over.
        Configure(dcMap: "");
        var c = Controller();

        StartResult r = await c.StartAsync();

        Assert.True(r.Outcome == StartOutcome.Started, $"{r.Outcome}: exit {r.ExitCode}\n{r.Output}");
        Assert.True(c.GetStatus().Listening);
        await c.StopAsync();
    }
}
