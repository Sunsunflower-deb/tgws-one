using System.Net;
using System.Net.Sockets;
using TgwsOne.Core;

namespace TgwsOne.Core.Tests;

public class InProcessEngineHostTests
{
    private const string SecretHex = "0123456789abcdef0123456789abcdef";

    private static void Configure(TempPaths temp, int port, string secret = SecretHex)
    {
        temp.Configure(ini =>
        {
            ini.Set("proxy", "host", "127.0.0.1");
            ini.Set("proxy", "port", port.ToString());
            ini.Set("proxy", "dcmap", "");
            ini.Set("proxy", "poolsize", "0");
            ini.Set("proxy", "cfproxy", "0");
            ini.Set("proxy", "h2", "0");
            ini.Set("proxy", "cfdomains", "example.test");
            ini.Set("app", "secret", secret);
        });
    }

    [Fact]
    public async Task StartsInsideThisProcess_StatusAndStop()
    {
        using var temp = new TempPaths();
        int port = Ports.Free();
        Configure(temp, port);
        var log = new AppLog(temp.Paths.AppLogFile);
        await using var host = new InProcessEngineHost(temp.Paths, log);

        StartResult started = await host.StartAsync();

        Assert.Equal(StartOutcome.Started, started.Outcome);
        Assert.Equal(port, started.Port);
        Assert.True(host.IsRunningInProcess);
        Assert.True(Ports.Listening(port));
        ProxyStatus status = host.GetStatus();
        Assert.True(status.Running);
        Assert.True(status.Listening);
        Assert.Equal(Environment.ProcessId, status.Pid);

        Assert.Equal(StartOutcome.AlreadyRunning, (await host.StartAsync()).Outcome);

        Assert.Equal(StopOutcome.Stopped, await host.StopAsync());
        Assert.False(host.IsRunningInProcess);
        Assert.True(await Ports.WaitForAsync(() => !Ports.Listening(port), TimeSpan.FromSeconds(5)));
        Assert.False(host.GetStatus().Running);
    }

    [Fact]
    public async Task BusyPort_SwitchesAndSavesTheChoice()
    {
        using var temp = new TempPaths();
        int busy = Ports.Free();
        Configure(temp, busy);
        using var blocker = new TcpListener(IPAddress.Loopback, busy);
        blocker.Start();

        var log = new AppLog(temp.Paths.AppLogFile);
        await using var host = new InProcessEngineHost(temp.Paths, log);

        StartResult started = await host.StartAsync();

        Assert.Equal(StartOutcome.PortSwitched, started.Outcome);
        Assert.Equal(busy, started.OldPort);
        Assert.InRange(started.Port, busy + 1, busy + 99);
        Assert.Equal(started.Port.ToString(), IniFile.Load(temp.Paths.Ini).Get("proxy", "port"));
        await host.StopAsync();
    }

    [Fact]
    public async Task RestartNeeded_WhenTheSecretChanges()
    {
        using var temp = new TempPaths();
        int port = Ports.Free();
        Configure(temp, port);
        var log = new AppLog(temp.Paths.AppLogFile);
        await using var host = new InProcessEngineHost(temp.Paths, log);
        await host.StartAsync();
        try
        {
            Assert.False(host.RestartNeeded());
            temp.Configure(ini => ini.Set("app", "secret", new string('f', 32)));
            Assert.True(host.RestartNeeded());
        }
        finally
        {
            await host.StopAsync();
        }
    }

    [Fact]
    public async Task ControlRequests_DriveTheHost()
    {
        using var temp = new TempPaths();
        int port = Ports.Free();
        Configure(temp, port);
        var log = new AppLog(temp.Paths.AppLogFile);
        await using var host = new InProcessEngineHost(temp.Paths, log);

        var started = await host.HandleControlAsync(new EngineControlRequest(EngineControlCommands.Start));
        Assert.True(started.Ok);
        Assert.Equal("Started", started.Outcome);
        Assert.Equal(port, started.Port);

        var status = await host.HandleControlAsync(new EngineControlRequest(EngineControlCommands.Status));
        Assert.True(status.Ok);
        Assert.True(status.Running);
        Assert.True(status.Listening);
        Assert.Equal(port, status.Port);

        var stopped = await host.HandleControlAsync(new EngineControlRequest(EngineControlCommands.Stop));
        Assert.True(stopped.Ok);
        Assert.Equal("Stopped", stopped.Outcome);
        Assert.False((await host.HandleControlAsync(new EngineControlRequest(EngineControlCommands.Status))).Running);

        var unknown = await host.HandleControlAsync(new EngineControlRequest("nonsense"));
        Assert.False(unknown.Ok);
    }
}
