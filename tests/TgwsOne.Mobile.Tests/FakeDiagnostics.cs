using TgwsOne.Engine;

namespace TgwsOne.Mobile.Tests;

/// <summary>The engine host as a test can set it: running, listening, on a port it names.</summary>
internal sealed class FakeEngineControl : IProxyEngineControl
{
    public EngineProxyStatus Status { get; set; } = new(Running: true, Listening: true, Port: 1443,
                                                          PortBusyByOther: false, Pid: 4242);
    public bool RestartIsNeeded { get; set; }

    public Task<EngineStartResult> StartAsync(CancellationToken ct = default)
        => Task.FromResult(new EngineStartResult(EngineStartOutcome.Started, Status.Port));

    public Task<EngineStopOutcome> StopAsync(CancellationToken ct = default)
        => Task.FromResult(EngineStopOutcome.Stopped);

    public EngineProxyStatus GetStatus() => Status;
    public bool RestartNeeded() => RestartIsNeeded;
}

/// <summary>All diagnostics probes with fixed answers and a log of what was asked.</summary>
internal sealed class DiagnosticProbes : IEgressProbes
{
    private readonly object _gate = new();
    private readonly List<(string Kind, string Host, int Port, string Route, TimeSpan Timeout)> _calls = [];

    public string CloudflareDomain { get; set; } = "cf.example";
    public int TcpOk { get; set; } = 5;
    public int TlsOk { get; set; } = 5;
    public int BypassTlsOk { get; set; } = 5;
    public bool CloudflareOk { get; set; } = true;
    public TimeSpan CloudflareTime { get; set; } = TimeSpan.FromMilliseconds(800);
    public Func<CancellationToken, Task>? BeforeProbe { get; set; }

    public IReadOnlyList<(string Kind, string Host, int Port, string Route, TimeSpan Timeout)> Calls
    {
        get { lock (_gate) return [.. _calls]; }
    }

    public (Uri Url, long Bytes, TimeSpan Timeout)? LastDownload { get; private set; }

    private static int Index(string host) => int.Parse(host["kws".Length..host.IndexOf('.')]) - 1;

    private async Task HoldAsync(CancellationToken ct)
    {
        if (BeforeProbe is { } hold) await hold(ct);
        ct.ThrowIfCancellationRequested();
    }

    public async Task<bool> TcpAsync(string host, int port, TimeSpan timeout, CancellationToken ct)
    {
        lock (_gate) _calls.Add(("tcp", host, port, "", timeout));
        await HoldAsync(ct);
        return Index(host) < TcpOk;
    }

    public async Task<TimeSpan?> TlsAsync(string host, int port, TimeSpan timeout, CancellationToken ct)
    {
        lock (_gate) _calls.Add(("tls", host, port, "", timeout));
        await HoldAsync(ct);
        return Index(host) < TlsOk ? TimeSpan.FromMilliseconds(100) : null;
    }

    public async Task<TimeSpan?> BypassHandshakeAsync(string host, int port, string route, TimeSpan timeout,
                                                      CancellationToken ct)
    {
        lock (_gate) _calls.Add(("bypass", host, port, route, timeout));
        await HoldAsync(ct);
        return Index(host) < BypassTlsOk ? TimeSpan.FromMilliseconds(100) : null;
    }

    public async Task<(bool Ok, TimeSpan Elapsed)> DownloadAsync(Uri url, long expectedBytes, TimeSpan timeout,
                                                                 CancellationToken ct)
    {
        lock (_gate) _calls.Add(("download", "", 0, "", timeout));
        await HoldAsync(ct);
        LastDownload = (url, expectedBytes, timeout);
        return (CloudflareOk, CloudflareTime);
    }

    public Task<bool> SelfCheckAsync(TimeSpan timeout, CancellationToken ct) => Task.FromResult(true);
}
