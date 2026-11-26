using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text;
using TgwsOne.Engine.Desync;
using TgwsOne.Engine.Transport;

namespace TgwsOne.Engine.Tests.Desync;

public sealed class EgressOutcomeTests
{
    private static readonly TimeSpan TestTimeout = TimeSpan.FromSeconds(20);

    [Fact]
    public async Task Attempts_LogOneLineEach_AndFillTheStatsAndTheController()
    {
        using var timeout = new CancellationTokenSource(TestTimeout);
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        int openPort = ((IPEndPoint)listener.LocalEndpoint).Port;
        int closedPort = ReserveClosedPort();
        Task accepting = AcceptAndHoldAsync(listener, timeout.Token);

        var lines = new ConcurrentQueue<string>();
        var controller = new RecordingController((_, route) =>
            route == "ws" ? new DesyncStrategy("split", []) : null);
        var stats = new EngineStats();
        var egress = new DesyncEgress(controller, lines.Enqueue, stats: stats);

        try
        {
            for (int i = 0; i < 2; i++)
            {
                await using Stream stream = await egress.ConnectAsync(
                    "127.0.0.1", openPort, "ws", "desync.example.test",
                    TimeSpan.FromSeconds(5), timeout.Token);
            }

            // A route whose controller answer is null still gets its own EGRESS line with strategy=none.
            await using (Stream stream = await egress.ConnectAsync(
                "127.0.0.1", openPort, "ws-fronted", "desync.example.test",
                TimeSpan.FromSeconds(5), timeout.Token))
            {
            }

            var failed = await Assert.ThrowsAsync<EgressException>(() => egress.ConnectAsync(
                "127.0.0.1", closedPort, "cf-worker", "desync.example.test",
                TimeSpan.FromSeconds(5), timeout.Token));
            Assert.Equal(EgressFailure.ConnectRefused, failed.Failure);
            Assert.Equal(EgressStage.Connect, failed.Stage);
            Assert.IsAssignableFrom<SocketException>(failed.InnerException);
        }
        finally
        {
            listener.Stop();
            timeout.Cancel();
            try { await accepting; } catch (Exception ex) when (ex is OperationCanceledException or SocketException) { }
        }

        Assert.Equal(4, stats.EgressAttempts);
        Assert.Equal(3, stats.EgressOk);
        Assert.Equal(1, stats.EgressConnectRefused);
        Assert.Equal(0, stats.EgressTlsAlert);
        Assert.Equal(0, stats.EgressUpgradeRejected);

        Assert.Equal(4, controller.Reports.Count);
        Assert.Equal(3, controller.Reports.Count(report => report.Ok));
        Report failedReport = Assert.Single(controller.Reports, report => !report.Ok);
        Assert.Equal(EgressFailure.ConnectRefused, failedReport.Failure);
        Assert.Null(failedReport.StrategyId);
        Assert.Equal("cf-worker", failedReport.Route);

        string[] journal = lines.Where(line => line.StartsWith("EGRESS ", StringComparison.Ordinal)).ToArray();
        Assert.Equal(4, journal.Length);
        Assert.Equal(2, journal.Count(line =>
            line.Contains("route=ws ", StringComparison.Ordinal) &&
            line.Contains("strategy=split ", StringComparison.Ordinal) &&
            line.Contains("result=ok ", StringComparison.Ordinal)));
        Assert.Contains(journal, line =>
            line.Contains("route=ws-fronted ", StringComparison.Ordinal) &&
            line.Contains("strategy=none ", StringComparison.Ordinal) &&
            line.Contains("result=ok ", StringComparison.Ordinal));
        Assert.Contains(journal, line =>
            line.Contains("route=cf-worker ", StringComparison.Ordinal) &&
            line.Contains("strategy=none ", StringComparison.Ordinal) &&
            line.Contains("result=ConnectRefused ", StringComparison.Ordinal) &&
            line.Contains("ms=", StringComparison.Ordinal));
    }

    [Fact]
    public async Task RawWebSocket_WithEgress_ThrowsATypedUpgradeFailure()
    {
        using var timeout = new CancellationTokenSource(TestTimeout);
        using var server = new FakeWsServer(RespondWith403Async);
        var egress = new DesyncEgress((_, _) => new DesyncStrategy("split", []));

        var exception = await Assert.ThrowsAsync<EgressException>(() => RawWebSocket.ConnectAsync(
            "127.0.0.1", "example.test", TimeSpan.FromSeconds(5), secure: false,
            port: server.Port, ct: timeout.Token, route: "ws", egress: egress));

        Assert.Equal(EgressFailure.UpgradeRejected, exception.Failure);
        Assert.Equal(EgressStage.Upgrade, exception.Stage);
        Assert.IsType<WsHandshakeException>(exception.InnerException);
    }

    [Fact]
    public async Task WithoutEgress_TheJournalAndTheCountersStayEmpty()
    {
        using var timeout = new CancellationTokenSource(TestTimeout);
        using var server = new FakeWsServer(RespondWith101Async);
        var lines = new ConcurrentQueue<string>();
        var controller = new RecordingController((_, _) => new DesyncStrategy("split", []));
        var stats = new EngineStats();

        // Built but not passed: a route without desync must not consult it, log through it or count through it.
        _ = new DesyncEgress(controller, lines.Enqueue, stats: stats);

        await using RawWebSocket ws = await RawWebSocket.ConnectAsync(
            "127.0.0.1", "example.test", TimeSpan.FromSeconds(5), secure: false,
            port: server.Port, ct: timeout.Token, route: "ws", egress: null);

        Assert.Empty(lines);
        Assert.Equal(0, stats.EgressAttempts);
        Assert.Equal(0, stats.EgressOk);
        Assert.Empty(controller.Reports);
    }

    private static int ReserveClosedPort()
    {
        var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        int port = ((IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();
        return port;
    }

    private static Task AcceptAndHoldAsync(TcpListener listener, CancellationToken ct)
        => Task.Run(async () =>
        {
            var held = new List<TcpClient>();
            try
            {
                while (!ct.IsCancellationRequested)
                    held.Add(await listener.AcceptTcpClientAsync(ct));
            }
            catch (Exception ex) when (ex is OperationCanceledException or SocketException or ObjectDisposedException)
            {
                // The test is over; dropping the held clients is the normal exit.
            }
            finally
            {
                foreach (TcpClient client in held) client.Dispose();
            }
        }, ct);

    private static async Task RespondWith101Async(NetworkStream stream, CancellationToken ct)
    {
        var buffer = new byte[1];
        var seen = new List<byte>();
        while (seen.Count < 16 * 1024)
        {
            int read = await stream.ReadAsync(buffer, ct);
            if (read == 0) return;
            seen.Add(buffer[0]);
            if (seen.Count >= 4 && seen[^4] == '\r' && seen[^3] == '\n' &&
                seen[^2] == '\r' && seen[^1] == '\n') break;
        }

        await stream.WriteAsync(Encoding.ASCII.GetBytes(
            "HTTP/1.1 101 Switching Protocols\r\nUpgrade: websocket\r\nConnection: Upgrade\r\n\r\n"), ct);
        await stream.FlushAsync(ct);
    }

    private static async Task RespondWith403Async(NetworkStream stream, CancellationToken ct)
    {
        var buffer = new byte[1];
        var seen = new List<byte>();
        while (seen.Count < 16 * 1024)
        {
            int read = await stream.ReadAsync(buffer, ct);
            if (read == 0) return;
            seen.Add(buffer[0]);
            if (seen.Count >= 4 && seen[^4] == '\r' && seen[^3] == '\n' &&
                seen[^2] == '\r' && seen[^1] == '\n') break;
        }

        await stream.WriteAsync(Encoding.ASCII.GetBytes(
            "HTTP/1.1 403 Forbidden\r\nContent-Length: 0\r\n\r\n"), ct);
        await stream.FlushAsync(ct);
    }

    private sealed record Report(string Sni, string Route, string? StrategyId, bool Ok, EgressFailure Failure);

    private sealed class RecordingController(Func<string, string, DesyncStrategy?> resolve) : IDesyncController
    {
        private readonly object _gate = new();
        private readonly List<Report> _reports = [];

        public IReadOnlyList<Report> Reports
        {
            get { lock (_gate) return _reports.ToArray(); }
        }

        public DesyncStrategy? Resolve(string sni, string route) => resolve(sni, route);

        public void Report(string sni, string route, string? strategyId, bool ok, EgressFailure failure,
                           TimeSpan elapsed)
        {
            lock (_gate) _reports.Add(new Report(sni, route, strategyId, ok, failure));
        }
    }

    private sealed class FakeWsServer : IDisposable
    {
        private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
        private readonly CancellationTokenSource _stop = new();
        private readonly Task _accepting;

        public FakeWsServer(Func<NetworkStream, CancellationToken, Task> handler)
        {
            _listener.Start();
            Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
            _accepting = Task.Run(async () =>
            {
                try
                {
                    using TcpClient client = await _listener.AcceptTcpClientAsync(_stop.Token);
                    await handler(client.GetStream(), _stop.Token);
                }
                catch (Exception ex) when (ex is OperationCanceledException or SocketException or IOException)
                {
                    // The test finishes and stops the listener; that is the normal exit.
                }
            });
        }

        public int Port { get; }

        public void Dispose()
        {
            _stop.Cancel();
            _listener.Stop();
            try { _accepting.Wait(TimeSpan.FromSeconds(2)); }
            catch (AggregateException) { }
            _stop.Dispose();
        }
    }
}
