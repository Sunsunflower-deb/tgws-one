namespace TgwsOne.Engine.Desync;

/// <summary>The strategy <c>auto</c> currently uses for one host and how many failures it has counted.</summary>
public sealed record AutoHost(string Host, string Strategy, int Failures);

/// <summary>
/// The live strategy selection for the engine. An explicit <see cref="Selected"/> strategy is handed out as-is;
/// when it is null the controller is in <c>auto</c> mode and rotates through <see cref="Rotation"/> per host.
/// The selection can change while the engine runs: every <see cref="Changed"/> listener is expected to drop idle
/// connections so the next one is made with the new strategy.
/// </summary>
public sealed class DesyncController : IDesyncController
{
    /// <summary>The <c>fails=</c> of the desktop <c>circular:fails=3:time=60</c> recipe.</summary>
    public const int DefaultMaxFailures = 3;

    /// <summary>The <c>time=</c> of the desktop <c>circular:fails=3:time=60</c> recipe.</summary>
    public static readonly TimeSpan DefaultFailureWindow = TimeSpan.FromSeconds(60);

    private readonly object _gate = new();
    private readonly Dictionary<string, HostState> _hosts = new(StringComparer.OrdinalIgnoreCase);
    private readonly TimeProvider _timeProvider;
    private readonly int _maxFailures;
    private readonly TimeSpan _failureWindow;
    private IReadOnlyList<DesyncStrategy> _rotation;

    /// <param name="rotation">The set <c>auto</c> walks; the first entry is where every host starts.</param>
    /// <param name="timeProvider">The clock the failure window is measured with; tests move a fake one.</param>
    /// <param name="maxFailures">Failures within <paramref name="failureWindow"/> before moving to the next strategy.</param>
    /// <param name="failureWindow">How far back a failure still counts.</param>
    public DesyncController(IReadOnlyList<DesyncStrategy>? rotation = null, TimeProvider? timeProvider = null,
                            int maxFailures = DefaultMaxFailures, TimeSpan? failureWindow = null)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(maxFailures, 1);
        _failureWindow = failureWindow ?? DefaultFailureWindow;
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(_failureWindow, TimeSpan.Zero);
        _timeProvider = timeProvider ?? TimeProvider.System;
        _maxFailures = maxFailures;
        _rotation = Snapshot(rotation ?? []);
    }

    /// <summary>An explicit strategy, or null for <c>auto</c>.</summary>
    public DesyncStrategy? Selected { get; private set; }

    /// <summary>True when no explicit strategy is selected and the rotation decides per host.</summary>
    public bool IsAuto => Selected is null;

    /// <summary>The set <c>auto</c> walks for the current operator, in order.</summary>
    public IReadOnlyList<DesyncStrategy> Rotation
    {
        get { lock (_gate) return _rotation; }
    }

    /// <summary>A snapshot of the per-host <c>auto</c> state, sorted by host.</summary>
    public IReadOnlyList<AutoHost> AutoHosts
    {
        get
        {
            lock (_gate)
            {
                if (_rotation.Count == 0) return [];
                return
                [
                    .. _hosts
                        .Where(pair => pair.Value.StrategyIndex >= 0 && pair.Value.StrategyIndex < _rotation.Count)
                        .Select(pair => new AutoHost(pair.Key, _rotation[pair.Value.StrategyIndex].Id,
                            pair.Value.Failures.Count))
                        .OrderBy(host => host.Host, StringComparer.OrdinalIgnoreCase)
                ];
            }
        }
    }

    /// <summary>Raised after the selection, the rotation or the per-host <c>auto</c> choice changed.</summary>
    public event Action? Changed;

    /// <summary>Selects an explicit strategy; null switches back to <c>auto</c>.</summary>
    public void Select(DesyncStrategy? strategy)
    {
        bool changed;
        lock (_gate)
        {
            if (ReferenceEquals(Selected, strategy)) return;
            Selected = strategy;
            _hosts.Clear();
            changed = true;
        }
        if (changed) Changed?.Invoke();
    }

    /// <summary>Switches back to <c>auto</c>.</summary>
    public void UseAuto() => Select(null);

    /// <summary>Replaces the rotation <c>auto</c> walks; per-host progress restarts from the first strategy.</summary>
    public void SetRotation(IReadOnlyList<DesyncStrategy> rotation)
    {
        ArgumentNullException.ThrowIfNull(rotation);
        lock (_gate)
        {
            _rotation = Snapshot(rotation);
            _hosts.Clear();
        }
        Changed?.Invoke();
    }

    public DesyncStrategy? Resolve(string sni, string route)
    {
        lock (_gate)
        {
            if (Selected is not null) return Selected;
            if (_rotation.Count == 0) return null;

            string host = Normalize(sni);
            if (!_hosts.TryGetValue(host, out HostState? state))
            {
                state = new HostState();
                _hosts[host] = state;
            }
            if (state.StrategyIndex >= _rotation.Count) state.StrategyIndex = 0;
            return _rotation[state.StrategyIndex];
        }
    }

    public void Report(string sni, string route, string? strategyId, bool ok, EgressFailure failure, TimeSpan elapsed)
    {
        // Only auto rotates. An explicit selection is the user's answer and must not be silently overruled by failures.
        _ = route;
        _ = failure;
        _ = elapsed;

        bool changed = false;
        lock (_gate)
        {
            if (Selected is not null || _rotation.Count == 0) return;

            string host = Normalize(sni);
            if (!_hosts.TryGetValue(host, out HostState? state))
            {
                state = new HostState();
                _hosts[host] = state;
            }
            if (state.StrategyIndex >= _rotation.Count) state.StrategyIndex = 0;

            // A late report from the strategy the host has already moved away from says nothing about the new one.
            string currentId = _rotation[state.StrategyIndex].Id;
            if (strategyId is not null && !string.Equals(strategyId, currentId, StringComparison.Ordinal)) return;

            if (ok)
            {
                if (state.Failures.Count > 0)
                {
                    state.Failures.Clear();
                    changed = true;
                }
                return;
            }

            DateTimeOffset now = _timeProvider.GetUtcNow();
            // Three failures inside the window rotate; a single old failure must not accumulate forever.
            int expired = 0;
            while (expired < state.Failures.Count && now - state.Failures[expired] > _failureWindow) expired++;
            if (expired > 0) state.Failures.RemoveRange(0, expired);
            state.Failures.Add(now);

            if (state.Failures.Count >= _maxFailures)
            {
                state.Failures.Clear();
                state.StrategyIndex = (state.StrategyIndex + 1) % _rotation.Count;
                changed = true;
            }
        }
        if (changed) Changed?.Invoke();
    }

    private static string Normalize(string? sni) => string.IsNullOrEmpty(sni) ? "" : sni;

    private static IReadOnlyList<DesyncStrategy> Snapshot(IReadOnlyList<DesyncStrategy> rotation)
        => rotation.Count == 0 ? [] : [.. rotation];

    private sealed class HostState
    {
        public int StrategyIndex;
        public readonly List<DateTimeOffset> Failures = [];
    }
}
