using TgwsOne.Services;
using TgwsOne.ViewModels;

namespace TgwsOne.Tests;

/// <summary>The real <see cref="BypassViewModel"/> and <see cref="StatusMonitor"/> over a fake service; the monitor polls only when asked (plus once at start).</summary>
internal sealed class BypassRig : IAsyncDisposable
{
    private readonly EditorKit _kit = new();
    private int _snapshots;   // the snapshots the page has been told about

    public FakeBypassService Service { get; } = new();
    public FakeProxyControl Proxy { get; } = new();
    public FakeNetInfo Net { get; } = new();
    public FakeDialogs Dialogs { get; } = new();
    public FakeClipboard Clipboard { get; } = new();
    public FakeProbes Probes { get; }
    public StatusMonitor Monitor { get; }
    public BypassViewModel Bypass { get; }

    /// <summary>The administrator, and the files they write (which here are the service's list of strategies).</summary>
    public FakeElevatedHelper Helper => _kit.Helper;

    /// <summary>The editors that were opened, with the name each was opened for (null: a new strategy).</summary>
    public List<string?> EditorsOpenedFor { get; } = [];

    public BypassRig()
    {
        Probes = new FakeProbes(Service);
        Monitor = new StatusMonitor(Proxy, Service, Net, TimeSpan.FromHours(1));
        // What the administrator saves or deletes is what the service lists afterwards.
        Helper.OnSaved = (name, _) => { if (!Service.StrategyIds.Contains("custom:" + name)) Service.StrategyIds.Add("custom:" + name); };
        Helper.OnDeleted = name => Service.StrategyIds.Remove("custom:" + name);
        Bypass = new BypassViewModel(Service, Monitor, Dialogs,
            () => new FinderViewModel(new StrategyFinder(Service, Probes, Probes, (_, _) => Task.CompletedTask)),
            name =>
            {
                EditorsOpenedFor.Add(name);
                return _kit.Editor(name);
            },
            Helper, Clipboard);
        // Subscribed after the page's own handler, and the handlers of one snapshot run one after the other: when this counts a snapshot, the
        // page has already applied it.
        Monitor.Changed += _ => Interlocked.Increment(ref _snapshots);
    }

    /// <summary>The page as the user finds it: the monitor has polled and the page has loaded its lists.</summary>
    public async Task OpenAsync()
    {
        // The monitor polls once by itself when it starts, and that poll may already have run before this rig subscribed to
        // Changed (on a fast machine) or may still be running (on a loaded one). Waiting for "its poll + ours = 2" therefore
        // raced it and failed as "the page never took the monitor's snapshots". What matters is that the page has applied a
        // snapshot before the lists are loaded; the refresh below is one, the monitor's own poll is another at most.
        await Monitor.RefreshNowAsync();
        await SettleAsync(1);
        await Bypass.LoadCommand.ExecuteAsync(null);
    }

    /// <summary>
    /// One poll now, and the page has applied what it found. <see cref="StatusMonitor.RefreshNowAsync"/> alone is not enough: the monitor
    /// raises <c>Changed</c> through the context it was created on, so it returns when the update is posted, and the page may rewrite its
    /// rows on another thread after that, under the test's feet.
    /// </summary>
    public async Task RefreshAsync()
    {
        int before = Volatile.Read(ref _snapshots);
        await Monitor.RefreshNowAsync();
        await SettleAsync(before + 1);
    }

    /// <summary>
    /// Waits for the real condition: at least <paramref name="atLeast"/> snapshots reached the page and, for a moment, no more came (the
    /// monitor also polls once by itself when it starts, at a time of its own choosing). The page does not change while this returns.
    /// </summary>
    private async Task SettleAsync(int atLeast)
    {
        // 15 s, not 5: a loaded CI runner can take seconds to get to a posted snapshot, and a false timeout here is a flaky build.
        DateTime limit = DateTime.UtcNow.AddSeconds(15);
        while (true)
        {
            if (DateTime.UtcNow > limit) throw new TimeoutException("the page never took the monitor's snapshots");
            int seen = Volatile.Read(ref _snapshots);
            if (seen >= atLeast)
            {
                await Task.Delay(30, TestContext.Current.CancellationToken);
                if (Volatile.Read(ref _snapshots) == seen) return;
            }
            else
            {
                await Task.Delay(10, TestContext.Current.CancellationToken);
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        await Monitor.DisposeAsync();
        _kit.Dispose();
    }
}
