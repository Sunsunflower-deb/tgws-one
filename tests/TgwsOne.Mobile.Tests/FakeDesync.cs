using TgwsOne.Engine.Desync;

namespace TgwsOne.Mobile.Tests;

/// <summary>What a strategy does to the network in a test: how many of the five Telegram handshakes get through, the CF-proxy handshake, the engine exchange, and how long it takes.</summary>
internal sealed record Outcome(int TelegramOk, bool CloudflareOk, bool SelfCheckOk, TimeSpan Time);

/// <summary>The engine's bypass as a test can drive it: what is applied, what it refuses, and what is put back.</summary>
internal sealed class FakeDesyncControl : IDesyncControl
{
    public IReadOnlyList<DesyncStrategy> Rotation { get; set; } = [];
    public IReadOnlyList<AutoHost> AutoHosts { get; set; } = [];
    public FinderBaseline Baseline { get; set; } = new(DesyncFinder.AutoId, true);
    public List<string> Applied { get; } = [];
    public int Restores { get; private set; }
    public string? Current { get; private set; } = DesyncFinder.AutoId;

    /// <summary>Returns a refusal for the candidate, or null to accept it.</summary>
    public Func<string, string?>? Refuse { get; set; }

    public Task<FinderBaseline> CaptureAsync(CancellationToken ct = default) => Task.FromResult(Baseline);

    public Task<string?> ApplyAsync(string candidateId, CancellationToken ct = default)
    {
        Applied.Add(candidateId);
        if (Refuse?.Invoke(candidateId) is { } problem) return Task.FromResult<string?>(problem);
        Current = candidateId;
        return Task.FromResult<string?>(null);
    }

    public Task<string?> RestoreAsync(FinderBaseline baseline, CancellationToken ct = default)
    {
        Restores++;
        Current = baseline.Strategy;
        return Task.FromResult<string?>(null);
    }

    public DesyncStrategy? Resolve(string sni, string route) => null;
}

/// <summary>
/// The finder's probes with answers looked up by the candidate the fake control is on right now, which is how a real
/// run behaves: the selected strategy decides what gets through.
/// </summary>
internal sealed class FinderProbes(FakeDesyncControl control) : IEgressProbes
{
    private readonly object _gate = new();
    private readonly List<(string Kind, string Host, int Port, string Route, TimeSpan Timeout)> _calls = [];

    public Dictionary<string, Outcome> Outcomes { get; } = [];
    public string CloudflareDomain { get; set; } = "cf.example";
    public Func<CancellationToken, Task>? BeforeProbe { get; set; }

    public IReadOnlyList<(string Kind, string Host, int Port, string Route, TimeSpan Timeout)> Calls
    {
        get { lock (_gate) return [.. _calls]; }
    }

    private Outcome Now => Outcomes.GetValueOrDefault(control.Current ?? "") ?? new(0, false, false, TimeSpan.Zero);

    private static int Index(string host) => int.Parse(host["kws".Length..host.IndexOf('.')]) - 1;

    private async Task HoldAsync(CancellationToken ct)
    {
        if (BeforeProbe is { } hold) await hold(ct);
        ct.ThrowIfCancellationRequested();
    }

    public Task<bool> TcpAsync(string host, int port, TimeSpan timeout, CancellationToken ct)
        => Task.FromResult(true);

    public Task<TimeSpan?> TlsAsync(string host, int port, TimeSpan timeout, CancellationToken ct)
        => Task.FromResult<TimeSpan?>(TimeSpan.FromMilliseconds(100));

    public async Task<TimeSpan?> BypassHandshakeAsync(string host, int port, string route, TimeSpan timeout,
                                                      CancellationToken ct)
    {
        lock (_gate) _calls.Add(("bypass", host, port, route, timeout));
        await HoldAsync(ct);

        Outcome o = Now;
        if (route == "cf-proxy") return o.CloudflareOk ? o.Time : null;
        return Index(host) < o.TelegramOk ? o.Time + TimeSpan.FromMilliseconds(10 * Index(host)) : null;
    }

    public Task<(bool Ok, TimeSpan Elapsed)> DownloadAsync(Uri url, long expectedBytes, TimeSpan timeout,
                                                            CancellationToken ct)
        => Task.FromResult((true, TimeSpan.FromMilliseconds(100)));

    public async Task<bool> SelfCheckAsync(TimeSpan timeout, CancellationToken ct)
    {
        lock (_gate) _calls.Add(("self", "", 0, "", timeout));
        await HoldAsync(ct);
        return Now.SelfCheckOk;
    }
}
