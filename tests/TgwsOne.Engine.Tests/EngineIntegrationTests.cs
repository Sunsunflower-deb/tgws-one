using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using TgwsOne.Core;
using TgwsOne.Engine.Crypto;
using TgwsOne.Engine.Relay;

namespace TgwsOne.Engine.Tests;

public class EngineIntegrationTests
{
    private const string SecretHex = "0123456789abcdef0123456789abcdef";
    private static readonly byte[] Secret = Convert.FromHexString(SecretHex);

    [Fact]
    public async Task Handshake_ThroughTcpFallback_ReachesAFakeTelegram_AndComesBack()
    {
        using var temp = new TempPaths();
        using var telegram = new FakeTelegram();
        int port = Ports.Free();

        var config = new EngineConfig
        {
            Host = "127.0.0.1",
            Port = port,
            Secret = SecretHex,
            DcRedirects = new Dictionary<int, string> { [2] = "127.0.0.1" },
            PoolSize = 0,
            FallbackCfProxy = false,
            CfproxyH2Media = false,
            CfproxyUserDomains = ["example.test"],
            LogFile = Path.Combine(temp.Root, "proxy.log"),
            TcpFallbackPort = telegram.Port,
            TcpFallbackHostOverride = "127.0.0.1",
        };
        await using var engine = new ProxyEngine(config);
        await engine.StartAsync(CancellationToken.None);
        try
        {
            var client = new ClientHandshake(Secret, MtProtoConstants.ProtoIntermediate, 2);
            using var tcp = new TcpClient();
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            await tcp.ConnectAsync(IPAddress.Loopback, port, timeout.Token);
            NetworkStream stream = tcp.GetStream();

            await stream.WriteAsync(client.Bytes, timeout.Token);

            byte[] payload = RandomNumberGenerator.GetBytes(32);
            byte[] packet = new byte[4 + payload.Length];
            BinaryPrimitives.WriteInt32LittleEndian(packet, payload.Length);
            payload.CopyTo(packet, 4);
            await stream.WriteAsync(client.Encrypt.Transform(packet), timeout.Token);
            await stream.FlushAsync(timeout.Token);

            await telegram.Received.Task.WaitAsync(TimeSpan.FromSeconds(5));

            byte[] echoed = new byte[packet.Length];
            await stream.ReadExactlyAsync(echoed, timeout.Token);
            Assert.Equal(packet, client.Decrypt.Transform(echoed));
            Assert.Equal(MtProtoConstants.ProtoIntermediate, telegram.ProtoTag);
            Assert.Equal(2, telegram.DcIdx);
            Assert.Equal(packet, telegram.Packet);
        }
        finally
        {
            await engine.StopAsync();
        }
        Assert.False(Ports.Listening(port));
    }

    [Fact]
    public async Task BadHandshake_IsDropped_AndTheListenerKeepsWorking()
    {
        using var temp = new TempPaths();
        int port = Ports.Free();
        var config = new EngineConfig
        {
            Host = "127.0.0.1",
            Port = port,
            Secret = SecretHex,
            DcRedirects = new Dictionary<int, string>(),
            PoolSize = 0,
            FallbackCfProxy = false,
            CfproxyH2Media = false,
            CfproxyUserDomains = ["example.test"],
            LogFile = Path.Combine(temp.Root, "proxy.log"),
        };
        await using var engine = new ProxyEngine(config);
        await engine.StartAsync(CancellationToken.None);
        try
        {
            using (var tcp = new TcpClient())
            {
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                await tcp.ConnectAsync(IPAddress.Loopback, port, timeout.Token);
                NetworkStream badStream = tcp.GetStream();
                await badStream.WriteAsync(new byte[64], timeout.Token);
                tcp.Client.Shutdown(SocketShutdown.Send);
                // The engine drains and closes; a read returns 0 (or the socket resets).
                var buffer = new byte[64];
                try
                {
                    int read = await badStream.ReadAsync(buffer, timeout.Token);
                    Assert.Equal(0, read);
                }
                catch (IOException)
                {
                    // reset is an acceptable way to be dropped
                }
            }

            Assert.True(engine.IsRunning);
            Assert.True(Ports.Listening(port));
        }
        finally
        {
            await engine.StopAsync();
        }
    }

    private sealed class FakeTelegram : IDisposable
    {
        private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
        private readonly CancellationTokenSource _stop = new();
        private readonly Task _serving;

        public int Port { get; }
        public uint ProtoTag { get; private set; }
        public short DcIdx { get; private set; }
        public byte[] Packet { get; private set; } = [];
        public TaskCompletionSource Received { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public FakeTelegram()
        {
            _listener.Start();
            Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
            _serving = Task.Run(async () =>
            {
              try
              {
                using TcpClient client = await _listener.AcceptTcpClientAsync(_stop.Token);
                NetworkStream stream = client.GetStream();
                var init = new byte[64];
                await stream.ReadExactlyAsync(init, _stop.Token);
                var telegram = new TelegramSide(init);
                ProtoTag = telegram.ProtoTag;
                DcIdx = telegram.DcIdx;

                byte[] wireHeader = new byte[4];
                await stream.ReadExactlyAsync(wireHeader, _stop.Token);
                byte[] header = telegram.DecryptFromProxy.Transform(wireHeader);
                int length = BinaryPrimitives.ReadInt32LittleEndian(header);
                if (length < 0 || length > 1_000_000)
                    throw new InvalidOperationException($"bad length {length}");
                byte[] wireBody = new byte[length];
                await stream.ReadExactlyAsync(wireBody, _stop.Token);
                byte[] body = telegram.DecryptFromProxy.Transform(wireBody);
                Packet = [.. header, .. body];

                byte[] reply = telegram.EncryptToProxy.Transform(Packet);
                await stream.WriteAsync(reply, _stop.Token);
                await stream.FlushAsync(_stop.Token);
                Received.TrySetResult();
                await Task.Delay(200, _stop.Token);
              }
              catch (Exception ex) { Received.TrySetException(ex); throw; }
            });
        }

        public void Dispose()
        {
            _stop.Cancel();
            _listener.Stop();
            try { _serving.Wait(TimeSpan.FromSeconds(2)); } catch (AggregateException) { }
            _stop.Dispose();
        }
    }
}

public class EngineControlChannelTests
{
    [Fact]
    public async Task ServerAndClient_ExchangeAStatusResponse()
    {
        using var temp = new TempPaths();
        await using EngineControlServer server = EngineControlServer.Start(temp.Paths, request =>
            Task.FromResult(request.Cmd == EngineControlCommands.Status
                ? new EngineControlResponse { Ok = true, Running = true, Listening = true, Port = 1443, Pid = 42 }
                : EngineControlResponse.Failure("unexpected")));
        await Task.Delay(100);   // the accept loop starts on a worker

        EngineControlResponse? response = await EngineControlClient.SendAsync(
            temp.Paths, new EngineControlRequest(EngineControlCommands.Status), TimeSpan.FromSeconds(5));

        Assert.NotNull(response);
        Assert.True(response!.Ok);
        Assert.True(response.Running);
        Assert.Equal(1443, response.Port);
        Assert.Equal(42, response.Pid);
    }

    /// <summary>A profile so deep in the temporary folder that its control socket does not fit in a Unix socket address
    /// (108 bytes at most): the listener cannot be created at all.</summary>
    private static TempPaths UnbindableProfile()
        => new(Path.Combine(new string('d', 100), "data"));

    [Fact]
    public async Task AListenerThatCannotBeCreated_IsReportToTheLog()
    {
        // The window learns that its control channel is down from this log line and from nothing else.
        using TempPaths temp = UnbindableProfile();
        var log = new List<string>();

        EngineControlServer server = EngineControlServer.Start(
            temp.Paths, _ => Task.FromResult(new EngineControlResponse { Ok = true }), log.Add);
        try
        {
            Assert.Single(log);
        }
        finally
        {
            await server.DisposeAsync();
        }
    }

    [Fact]
    public async Task AServerWhoseListenerCouldNotBeCreated_DisposesWithoutThrowing()
    {
        // The application disposes this server on its way out, before it stops the engine: an exception here would
        // end the process with a crash instead.
        using TempPaths temp = UnbindableProfile();

        EngineControlServer server = EngineControlServer.Start(
            temp.Paths, _ => Task.FromResult(new EngineControlResponse { Ok = true }));

        await server.DisposeAsync();
    }

    [Fact]
    public async Task NoServer_ReturnsNull_WithoutHanging()
    {
        using var temp = new TempPaths();
        var started = System.Diagnostics.Stopwatch.StartNew();

        EngineControlResponse? response = await EngineControlClient.SendAsync(
            temp.Paths, new EngineControlRequest(EngineControlCommands.Status), TimeSpan.FromMilliseconds(300));

        Assert.Null(response);
        Assert.True(started.Elapsed < TimeSpan.FromSeconds(3));
    }
}

/// <summary>
/// The CLI path when the window is running: ProxyController asks the window's control channel, and the window
/// starts/stops its in-process engine. This is what `tgwsone --start` does from a terminal next to the window.
/// </summary>
public class CliToWindowControlTests
{
    private const string SecretHex = "0123456789abcdef0123456789abcdef";

    [Fact]
    public async Task ProxyController_DrivesTheHostedEngine_WithoutStartingAProcess()
    {
        using var temp = new TempPaths();
        int port = Ports.Free();
        temp.Configure(ini =>
        {
            ini.Set("proxy", "host", "127.0.0.1");
            ini.Set("proxy", "port", port.ToString());
            ini.Set("proxy", "dcmap", "");
            ini.Set("proxy", "poolsize", "0");
            ini.Set("proxy", "cfproxy", "0");
            ini.Set("proxy", "h2", "0");
            ini.Set("proxy", "cfdomains", "example.test");
            ini.Set("app", "secret", SecretHex);
        });

        var log = new AppLog(temp.Paths.AppLogFile);
        await using var host = new InProcessEngineHost(temp.Paths, log);
        await using EngineControlServer server = EngineControlServer.Start(
            temp.Paths, request => host.HandleControlAsync(request), log.Write);
        await Task.Delay(100);

        var controller = new ProxyController(temp.Paths, log, TimeSpan.FromSeconds(10));

        StartResult started = await controller.StartAsync();
        Assert.Equal(StartOutcome.Started, started.Outcome);
        Assert.Equal(port, started.Port);
        Assert.True(host.IsRunningInProcess);
        Assert.False(File.Exists(temp.Paths.PidFile));   // no detached engine to record
        Assert.True(Ports.Listening(port));

        ProxyStatus status = controller.GetStatus();
        Assert.True(status.Running);
        Assert.True(status.Listening);
        Assert.Equal(port, status.Port);
        Assert.False(controller.RestartNeeded());

        Assert.Equal(StopOutcome.Stopped, await controller.StopAsync());
        Assert.False(host.IsRunningInProcess);
        Assert.True(await Ports.WaitForAsync(() => !Ports.Listening(port), TimeSpan.FromSeconds(5)));
    }
}

public class PoolWarmupTests
{
    private const string SecretHex = "0123456789abcdef0123456789abcdef";

    /// <summary>
    /// Warmup schedules a bucket that does not exist yet; this used to index the dictionary directly and threw
    /// KeyNotFoundException, which would have aborted every real start with the default pool.
    /// </summary>
    [Fact]
    public async Task StartingWithAWarmPool_DoesNotThrow_AndStops()
    {
        using var temp = new TempPaths();
        int port = Ports.Free();
        var config = new EngineConfig
        {
            Host = "127.0.0.1",
            Port = port,
            Secret = SecretHex,
            DcRedirects = new Dictionary<int, string> { [2] = "127.0.0.1" },
            PoolSize = 1,
            FallbackCfProxy = false,
            CfproxyH2Media = false,
            CfproxyUserDomains = ["example.test"],
            CfproxyWorkerDomains = ["127.0.0.1"],
            LogFile = Path.Combine(temp.Root, "proxy.log"),
            TcpFallbackHostOverride = "127.0.0.1",
        };
        await using var engine = new ProxyEngine(config);

        await engine.StartAsync(CancellationToken.None);

        Assert.True(engine.IsRunning);
        Assert.True(Ports.Listening(port));
        await engine.StopAsync();
        Assert.False(Ports.Listening(port));
    }
}
