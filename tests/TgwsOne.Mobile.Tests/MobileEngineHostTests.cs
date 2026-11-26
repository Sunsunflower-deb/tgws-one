using System.Net;
using System.Net.Sockets;
using TgwsOne.Core;
using TgwsOne.Engine;

namespace TgwsOne.Mobile.Tests;

public class MobileEngineHostTests
{
    private const string SecretHex = "0123456789abcdef0123456789abcdef";

    private static void Configure(TempFiles temp, int port, string secret = SecretHex)
    {
        temp.Configure(ini =>
        {
            ini.Set("proxy", "host", "127.0.0.1");
            ini.Set("proxy", "port", port.ToString());
            ini.Set("proxy", "dcmap", "");
            ini.Set("proxy", "poolsize", "0");
            ini.Set("proxy", "cfproxy", "0");
            ini.Set("proxy", "h2", "0");
            ini.Set("proxy", "faketls", "");
            ini.Set("app", "secret", secret);
        });
    }

    [Fact]
    public async Task StartsInsideThisProcess_StatusAndStop()
    {
        using var temp = new TempFiles();
        int port = MobilePorts.Free();
        Configure(temp, port);
        await using var host = new MobileEngineHost(temp.Storage);

        EngineStartResult started = await host.StartAsync();

        Assert.Equal(EngineStartOutcome.Started, started.Outcome);
        Assert.Equal(port, started.Port);
        Assert.True(host.IsRunning);
        Assert.True(MobilePorts.Listening(port));
        EngineProxyStatus status = host.GetStatus();
        Assert.True(status.Running);
        Assert.True(status.Listening);
        Assert.Equal(port, status.Port);
        Assert.Equal(Environment.ProcessId, status.Pid);

        Assert.Equal(EngineStartOutcome.AlreadyRunning, (await host.StartAsync()).Outcome);

        Assert.Equal(EngineStopOutcome.Stopped, await host.StopAsync());
        Assert.False(host.IsRunning);
        Assert.True(await MobilePorts.WaitForAsync(() => !MobilePorts.Listening(port), TimeSpan.FromSeconds(5)));
        Assert.False(host.GetStatus().Running);
        Assert.Equal(EngineStopOutcome.NotRunning, await host.StopAsync());
    }

    [Fact]
    public async Task BusyPort_SwitchesAndSavesTheChoice()
    {
        using var temp = new TempFiles();
        int busy = MobilePorts.Free();
        Configure(temp, busy);
        using var blocker = new TcpListener(IPAddress.Loopback, busy);
        blocker.Start();

        await using var host = new MobileEngineHost(temp.Storage);
        EngineStartResult started = await host.StartAsync();

        Assert.Equal(EngineStartOutcome.PortSwitched, started.Outcome);
        Assert.Equal(busy, started.OldPort);
        Assert.InRange(started.Port, busy + 1, busy + 99);
        Assert.Equal(started.Port.ToString(), IniFile.Load(temp.Storage.IniPath).Get("proxy", "port"));
        await host.StopAsync();
    }

    [Fact]
    public async Task RestartNeeded_WhenTheSecretChanges()
    {
        using var temp = new TempFiles();
        int port = MobilePorts.Free();
        Configure(temp, port);
        await using var host = new MobileEngineHost(temp.Storage);
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
    public async Task RestartNeeded_FollowsTheAndroidSection()
    {
        using var temp = new TempFiles();
        int port = MobilePorts.Free();
        Configure(temp, port);
        await using var host = new MobileEngineHost(temp.Storage);
        await host.StartAsync();
        try
        {
            Assert.False(host.RestartNeeded());
            temp.Configure(ini => AndroidSettingsStore.Save(ini, AndroidSettings.Default with { PoolIdle = 60 }));
            Assert.True(host.RestartNeeded());
        }
        finally
        {
            await host.StopAsync();
        }
    }

    [Fact]
    public async Task Start_MakesAndKeepsAValidSecret()
    {
        using var temp = new TempFiles();
        int port = MobilePorts.Free();
        Configure(temp, port, secret: "");
        await using var host = new MobileEngineHost(temp.Storage);

        await host.StartAsync();
        try
        {
            string? secret = IniFile.Load(temp.Storage.IniPath).Get("app", "secret");
            Assert.True(ProxySettingsStore.IsValidSecret(secret));
        }
        finally
        {
            await host.StopAsync();
        }
    }

    [Fact]
    public async Task RestartNeeded_IsFalseWhileStopped()
    {
        using var temp = new TempFiles();
        Configure(temp, MobilePorts.Free());
        await using var host = new MobileEngineHost(temp.Storage);

        Assert.False(host.RestartNeeded());
    }
}
