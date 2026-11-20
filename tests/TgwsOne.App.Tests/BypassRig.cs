using TgwsOne.Services;
using TgwsOne.ViewModels;

namespace TgwsOne.Tests;

/// <summary>The real <see cref="BypassViewModel"/> and <see cref="StatusMonitor"/> over a fake service; the monitor polls only when asked (plus once at start).</summary>
internal sealed class BypassRig : IAsyncDisposable
{
    public FakeBypassService Service { get; } = new();
    public FakeProxyControl Proxy { get; } = new();
    public FakeNetInfo Net { get; } = new();
    public FakeDialogs Dialogs { get; } = new();
    public FakeProbes Probes { get; }
    public StatusMonitor Monitor { get; }
    public BypassViewModel Bypass { get; }

    public BypassRig()
    {
        Probes = new FakeProbes(Service);
        Monitor = new StatusMonitor(Proxy, Service, Net, TimeSpan.FromHours(1));
        Bypass = new BypassViewModel(Service, Monitor, Dialogs,
            () => new FinderViewModel(new StrategyFinder(Service, Probes, Probes, (_, _) => Task.CompletedTask)));
    }

    /// <summary>The page as the user finds it: the monitor has polled and the page has loaded its lists.</summary>
    public async Task OpenAsync()
    {
        await Monitor.RefreshNowAsync();
        await Bypass.LoadCommand.ExecuteAsync(null);
    }

    public ValueTask DisposeAsync() => Monitor.DisposeAsync();
}
