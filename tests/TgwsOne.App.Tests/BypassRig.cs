using TgwsOne.Services;
using TgwsOne.ViewModels;

namespace TgwsOne.Tests;

/// <summary>The real <see cref="BypassViewModel"/> and <see cref="StatusMonitor"/> over a fake service; the monitor polls only when asked (plus once at start).</summary>
internal sealed class BypassRig : IAsyncDisposable
{
    private readonly EditorKit _kit = new();

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
    }

    /// <summary>The page as the user finds it: the monitor has polled and the page has loaded its lists.</summary>
    public async Task OpenAsync()
    {
        await Monitor.RefreshNowAsync();
        await Bypass.LoadCommand.ExecuteAsync(null);
    }

    public async ValueTask DisposeAsync()
    {
        await Monitor.DisposeAsync();
        _kit.Dispose();
    }
}
