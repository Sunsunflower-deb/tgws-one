using TgwsOne.Services;

namespace TgwsOne.Tests;

/// <summary>All three probes with fixed answers: how many of the five Telegram servers answer on TCP and on TLS, and whether the download arrives whole.</summary>
internal sealed class FixedProbes : ITcpProbe, ITlsProbe, IHttpProbe
{
    private readonly object _gate = new();
    private readonly List<(string Kind, string Host, int Port, TimeSpan Timeout)> _calls = [];
    private int _downloads;

    public int TcpOk { get; set; } = 5;
    public int TlsOk { get; set; } = 5;
    public bool CloudflareOk { get; set; } = true;
    public TimeSpan CloudflareTime { get; set; } = TimeSpan.FromMilliseconds(800);

    /// <summary>Runs first in every probe (to hold probes back, or to count how many are in flight).</summary>
    public Func<CancellationToken, Task>? BeforeProbe { get; set; }

    public IReadOnlyList<(string Kind, string Host, int Port, TimeSpan Timeout)> Calls { get { lock (_gate) return [.. _calls]; } }
    public (Uri Url, long Bytes, TimeSpan Timeout)? LastDownload { get; private set; }
    public int Downloads => Volatile.Read(ref _downloads);

    private static int Index(string host) => int.Parse(host["kws".Length..host.IndexOf('.')]) - 1;   // kws1 → 0

    public async Task<bool> ConnectAsync(string host, int port, TimeSpan timeout, CancellationToken ct)
    {
        lock (_gate) _calls.Add(("tcp", host, port, timeout));
        if (BeforeProbe is { } hold) await hold(ct);
        return Index(host) < TcpOk;
    }

    public async Task<TimeSpan?> HandshakeAsync(string host, int port, TimeSpan timeout, CancellationToken ct)
    {
        lock (_gate) _calls.Add(("tls", host, port, timeout));
        if (BeforeProbe is { } hold) await hold(ct);
        return Index(host) < TlsOk ? TimeSpan.FromMilliseconds(100) : null;
    }

    public async Task<(bool Ok, TimeSpan Elapsed)> DownloadAsync(Uri url, long expectedBytes, TimeSpan timeout, CancellationToken ct)
    {
        Interlocked.Increment(ref _downloads);
        LastDownload = (url, expectedBytes, timeout);
        if (BeforeProbe is { } hold) await hold(ct);
        return (CloudflareOk, CloudflareTime);
    }
}
