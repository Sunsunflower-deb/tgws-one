using System.Net.Sockets;
using System.Text;
using TgwsOne.Core.Zapret;

namespace TgwsOne.Core.Tests.Zapret;

/// <summary>A fact for the Unix-socket half of the control channel; the Windows pipe is exercised in SP5.</summary>
public sealed class LinuxFactAttribute : FactAttribute
{
    public LinuxFactAttribute()
    {
        if (!OperatingSystem.IsLinux()) Skip = "Unix socket test";
    }
}

public class ControlServerTests
{
    private sealed class StubHandler(Func<ControlRequest, Task<ControlResponse>>? handle = null) : IControlHandler
    {
        private int _running;
        private int _maxRunning;
        private readonly List<ControlRequest> _calls = [];

        public IReadOnlyList<ControlRequest> Calls { get { lock (_calls) return [.. _calls]; } }
        public int MaxConcurrent => _maxRunning;

        public async Task<ControlResponse> HandleAsync(ControlRequest request, CancellationToken ct)
        {
            lock (_calls) _calls.Add(request);
            int now = Interlocked.Increment(ref _running);
            int seen;
            while ((seen = _maxRunning) < now && Interlocked.CompareExchange(ref _maxRunning, now, seen) != seen) { }
            try
            {
                return handle is null ? new ControlResponse(true, Current: request.Cmd) : await handle(request);
            }
            finally
            {
                Interlocked.Decrement(ref _running);
            }
        }
    }

    private static byte[] Line(string json) => Encoding.UTF8.GetBytes(json + "\n");

    private sealed class Fixture : IAsyncDisposable
    {
        private readonly TempDir _tmp;
        private readonly CancellationTokenSource _cts = new();
        private readonly Task _run;

        private Fixture(TempDir tmp, ServicePaths paths, StubHandler handler, ControlServer server)
        {
            _tmp = tmp;
            Paths = paths;
            Handler = handler;
            _run = server.RunAsync(_cts.Token);
            Listening = server.WhenListening;
        }

        public ServicePaths Paths { get; }
        public StubHandler Handler { get; }
        public string Socket => Paths.Endpoint;
        private Task Listening { get; }

        public static async Task<Fixture> StartAsync(Func<ControlRequest, Task<ControlResponse>>? handle = null, TimeSpan? readTimeout = null, Action<string>? beforeStart = null)
        {
            var tmp = new TempDir();
            string root = tmp.Combine("root");
            Directory.CreateDirectory(root);
            ServicePaths paths = ServicePaths.For(OsKind.Linux, n => n == "TGWS_SERVICE_ROOT" ? root : null);
            beforeStart?.Invoke(paths.Endpoint);
            var handler = new StubHandler(handle);
            var server = new ControlServer(paths, handler, new AppLog(Path.Combine(root, "test.log")), readTimeout);
            var f = new Fixture(tmp, paths, handler, server);
            await f.Listening;
            return f;
        }

        public ControlClient Client(TimeSpan? timeout = null) => new(Paths, timeout);

        /// <summary>Sends raw bytes and returns the first response line (null if the server closed without answering).</summary>
        public async Task<string?> RawAsync(byte[] data, bool shutdownSend = false, int waitMs = 5000)
        {
            using var s = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
            await s.ConnectAsync(new UnixDomainSocketEndPoint(Socket));
            using var cts = new CancellationTokenSource(waitMs);
            await using var stream = new NetworkStream(s);
            await stream.WriteAsync(data, cts.Token);
            if (shutdownSend) s.Shutdown(SocketShutdown.Send);
            var sb = new StringBuilder();
            var buf = new byte[4096];
            while (true)
            {
                int n;
                try { n = await stream.ReadAsync(buf, cts.Token); }
                catch (IOException) { break; }
                if (n == 0) break;
                sb.Append(Encoding.UTF8.GetString(buf, 0, n));
                if (sb.ToString().Contains('\n')) break;
            }
            return sb.Length == 0 ? null : sb.ToString().TrimEnd('\n');
        }

        public async ValueTask DisposeAsync()
        {
            await _cts.CancelAsync();
            await _run;
            _cts.Dispose();
            _tmp.Dispose();
        }
    }

    [LinuxFact]
    public async Task NormalRequest_ReachesTheHandler_AndIsAnswered()
    {
        await using var f = await Fixture.StartAsync(r => Task.FromResult(new ControlResponse(true, Current: r.Strategy)));

        ControlResponse r = await f.Client().SendAsync(new ControlRequest("use", Strategy: "split"));

        Assert.True(r.Ok);
        Assert.Equal("split", r.Current);
        Assert.Equal(["use"], f.Handler.Calls.Select(c => c.Cmd));
    }

    [LinuxFact]
    public async Task UnknownCommand_IsAnErrorAndNeverReachesTheHandler()
    {
        await using var f = await Fixture.StartAsync();

        string? line = await f.RawAsync(Line("""{"cmd":"reboot"}"""));

        ControlResponse r = ControlProtocol.ParseResponse(line!);
        Assert.False(r.Ok);
        Assert.Equal("неизвестная команда", r.Error);
        Assert.Empty(f.Handler.Calls);
    }

    [LinuxFact]
    public async Task MalformedJson_IsAnError_AndTheServerKeepsServing()
    {
        await using var f = await Fixture.StartAsync();

        ControlResponse bad = ControlProtocol.ParseResponse((await f.RawAsync(Line("{\"cmd\": nope")))!);
        Assert.False(bad.Ok);
        Assert.Equal("неверный запрос", bad.Error);

        ControlResponse good = await f.Client().SendAsync(new ControlRequest("status"));
        Assert.True(good.Ok);
    }

    [LinuxFact]
    public async Task OversizeLine_IsRefused_EvenWhenTheClientKeepsSendingPastTheSocketBuffer()
    {
        await using var f = await Fixture.StartAsync();
        byte[] huge = Line("{\"cmd\":\"use\",\"strategy\":\"" + new string('a', 1_200_000) + "\"}");   // well past MaxLineBytes and any socket buffer

        string? line = await f.RawAsync(huge);

        ControlResponse r = ControlProtocol.ParseResponse(line!);
        Assert.False(r.Ok);
        Assert.Equal("слишком длинный запрос", r.Error);
        Assert.Empty(f.Handler.Calls);
    }

    [LinuxFact]
    public async Task LineLimit_IsMaxLineBytes_ExcludingTheNewline()
    {
        await using var f = await Fixture.StartAsync();
        const string head = "{\"cmd\":\"use\",\"strategy\":\"";
        const string tail = "\"}";

        string Pad(int total) => head + new string('a', total - head.Length - tail.Length) + tail;
        string atLimit = Pad(ControlProtocol.MaxLineBytes);
        string over = Pad(ControlProtocol.MaxLineBytes + 1);

        ControlResponse ok = ControlProtocol.ParseResponse((await f.RawAsync(Encoding.UTF8.GetBytes(atLimit + "\n")))!);
        Assert.True(ok.Ok);

        ControlResponse tooBig = ControlProtocol.ParseResponse((await f.RawAsync(Encoding.UTF8.GetBytes(over + "\n")))!);
        Assert.Equal("слишком длинный запрос", tooBig.Error);
        Assert.Single(f.Handler.Calls);
    }

    [LinuxFact]
    public async Task TheLargestValidSetExclusions_TravelsThroughTheClientAndTheServer()
    {
        // A real client and a real server: about 300 KB, which the old 64 KB limit refused.
        await using var f = await Fixture.StartAsync();

        ControlResponse r = await f.Client().SendAsync(MaximalExclusions.Request());

        Assert.True(r.Ok, r.Error);
        ControlRequest got = Assert.Single(f.Handler.Calls);
        Assert.Equal(ControlCommands.SetExclusions, got.Cmd);
        Assert.Equal(Exclusions.MaxEntries, got.Hosts!.Count);
        Assert.Equal(Exclusions.MaxEntries, got.Ips!.Count);
    }

    [LinuxFact]
    public async Task CrLf_AndMissingFinalNewline_AreAccepted()
    {
        await using var f = await Fixture.StartAsync();

        ControlResponse crlf = ControlProtocol.ParseResponse((await f.RawAsync(Encoding.UTF8.GetBytes("{\"cmd\":\"status\"}\r\n")))!);
        Assert.True(crlf.Ok);

        ControlResponse eof = ControlProtocol.ParseResponse((await f.RawAsync(Encoding.UTF8.GetBytes("{\"cmd\":\"on\"}"), shutdownSend: true))!);
        Assert.True(eof.Ok);

        Assert.Equal(["status", "on"], f.Handler.Calls.Select(c => c.Cmd));
    }

    [LinuxFact]
    public async Task NothingListening_IsServiceUnavailable()
    {
        using var tmp = new TempDir();
        ServicePaths paths = ServicePaths.For(OsKind.Linux, n => n == "TGWS_SERVICE_ROOT" ? tmp.Path : null);

        var ex = await Assert.ThrowsAsync<ServiceUnavailableException>(() => new ControlClient(paths, TimeSpan.FromSeconds(2)).SendAsync(new ControlRequest("status")));

        Assert.Equal("служба обхода DPI недоступна", ex.Message);
    }

    [LinuxFact]
    public async Task StaleSocketFile_IsServiceUnavailable()
    {
        using var tmp = new TempDir();
        ServicePaths paths = ServicePaths.For(OsKind.Linux, n => n == "TGWS_SERVICE_ROOT" ? tmp.Path : null);
        // .NET unlinks a bound socket's path when the socket is disposed; moving the file away first leaves the
        // inode behind with nobody listening, which is what a crashed service leaves.
        string aside = paths.Endpoint + ".aside";
        using (var dead = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified))
        {
            dead.Bind(new UnixDomainSocketEndPoint(paths.Endpoint));
            dead.Listen(1);
            File.Move(paths.Endpoint, aside);
        }
        File.Move(aside, paths.Endpoint);
        Assert.True(File.Exists(paths.Endpoint));

        await Assert.ThrowsAsync<ServiceUnavailableException>(() => new ControlClient(paths, TimeSpan.FromSeconds(2)).SendAsync(new ControlRequest("status")));
    }

    [LinuxFact]
    public async Task ServerThatNeverAnswers_TimesOutTheClient()
    {
        await using var f = await Fixture.StartAsync(async _ => { await Task.Delay(3000); return new ControlResponse(true); });

        var sw = System.Diagnostics.Stopwatch.StartNew();
        var ex = await Assert.ThrowsAsync<ServiceUnavailableException>(() => f.Client(TimeSpan.FromMilliseconds(300)).SendAsync(new ControlRequest("status")));

        Assert.True(sw.ElapsedMilliseconds < 2500, $"took {sw.ElapsedMilliseconds} ms");
        Assert.Equal("служба обхода DPI не отвечает", ex.Message);
    }

    [LinuxFact]
    public async Task TwoConcurrentClients_BothAnswered_HandlerCallsAreSerialized()
    {
        await using var f = await Fixture.StartAsync(async r =>
        {
            await Task.Delay(150);
            return new ControlResponse(true, Current: r.Strategy);
        });

        Task<ControlResponse> a = f.Client().SendAsync(new ControlRequest("use", Strategy: "one"));
        Task<ControlResponse> b = f.Client().SendAsync(new ControlRequest("use", Strategy: "two"));
        ControlResponse[] both = await Task.WhenAll(a, b);

        Assert.Equal(["one", "two"], both.Select(r => r.Current));
        Assert.Equal(1, f.Handler.MaxConcurrent);
    }

    [LinuxFact]
    public async Task SilentClient_DoesNotBlockOthers_AndIsDroppedAfterTheReadTimeout()
    {
        await using var f = await Fixture.StartAsync(readTimeout: TimeSpan.FromMilliseconds(300));
        using var silent = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        await silent.ConnectAsync(new UnixDomainSocketEndPoint(f.Socket));

        ControlResponse r = await f.Client(TimeSpan.FromSeconds(2)).SendAsync(new ControlRequest("status"));
        Assert.True(r.Ok);

        // The server hangs up on the silent connection once the read timeout has passed.
        silent.ReceiveTimeout = 3000;
        Assert.Equal(0, silent.Receive(new byte[16]));
        Assert.DoesNotContain(f.Handler.Calls, c => c.Cmd != "status");
    }

    [LinuxFact]
    public async Task SocketFile_IsWorldReadableAndWritable()
    {
        if (OperatingSystem.IsWindows()) return;   // the fact is skipped there; this tells the analyzer
        await using var f = await Fixture.StartAsync();

        UnixFileMode mode = File.GetUnixFileMode(f.Socket);

        Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead | UnixFileMode.GroupWrite | UnixFileMode.OtherRead | UnixFileMode.OtherWrite,
            mode & (UnixFileMode)0x1FF);
    }

    [LinuxFact]
    public async Task StaleSocketFile_IsReplacedOnStart()
    {
        await using var f = await Fixture.StartAsync(beforeStart: path => File.WriteAllText(path, "left over from a crash"));

        ControlResponse r = await f.Client().SendAsync(new ControlRequest("status"));

        Assert.True(r.Ok);
    }

    [LinuxFact]
    public async Task HandlerThrows_IsAnErrorResponse_AndTheServerKeepsServing()
    {
        int n = 0;
        await using var f = await Fixture.StartAsync(_ => Interlocked.Increment(ref n) == 1
            ? throw new InvalidOperationException("kaboom")
            : Task.FromResult(new ControlResponse(true)));

        ControlResponse first = await f.Client().SendAsync(new ControlRequest("status"));
        ControlResponse second = await f.Client().SendAsync(new ControlRequest("status"));

        Assert.False(first.Ok);
        Assert.Equal("внутренняя ошибка службы", first.Error);
        Assert.DoesNotContain("kaboom", first.Error);
        Assert.True(second.Ok);
    }

    [LinuxFact]
    public async Task Cancellation_StopsTheServer_AndRemovesTheSocket()
    {
        using var tmp = new TempDir();
        ServicePaths paths = ServicePaths.For(OsKind.Linux, n => n == "TGWS_SERVICE_ROOT" ? tmp.Path : null);
        var server = new ControlServer(paths, new StubHandler(), new AppLog(tmp.Combine("test.log")));
        using var cts = new CancellationTokenSource();
        Task run = server.RunAsync(cts.Token);
        await server.WhenListening;
        Assert.True(File.Exists(paths.Endpoint));

        await cts.CancelAsync();
        await run.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.False(File.Exists(paths.Endpoint));
    }
}
