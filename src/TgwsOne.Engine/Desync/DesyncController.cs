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
    private readonly object _gate = new();
    private readonly Dictionary<string, HostState> _hosts = new(StringComparer.OrdinalIgnoreCase);
    private IReadOnlyList<DesyncStrategy> _rotation;

    public DesyncController(IReadOnlyList<DesyncStrategy>? rotation = null)
    {
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
        // The failure window and the rotation move live in E3.2; the selection itself is already useful without them.
        _ = sni;
        _ = route;
        _ = strategyId;
        _ = ok;
        _ = failure;
        _ = elapsed;
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
