using TgwsOne.Services;
using TgwsOne.ViewModels;

namespace TgwsOne.Tests;

/// <summary>The real <see cref="HomeViewModel"/> and <see cref="StatusMonitor"/> over fakes; the monitor polls only when asked (plus once at start).</summary>
internal sealed class HomeRig : IAsyncDisposable
{
    public FakeProxyControl Proxy { get; } = new();
    public FakeControlClient Control { get; } = new();
    public FakeNetInfo Net { get; } = new();
    public FakeAppSettings Settings { get; } = new();
    public FakeLauncher Launcher { get; } = new();
    public FakeClipboard Clipboard { get; } = new();
    public FakeDialogs Dialogs { get; } = new();
    public FakeNavigator Navigator { get; } = new();
    public StatusMonitor Monitor { get; }
    public HomeViewModel Home { get; }

    public HomeRig()
    {
        Monitor = new StatusMonitor(Proxy, Control, Net, TimeSpan.FromHours(1));
        Home = new HomeViewModel(Proxy, Control, Monitor, Settings, Launcher, Clipboard, Dialogs, Navigator,
                                 () => new QrViewModel(Settings, Net, Clipboard));
    }

    /// <summary>How many times the service was told <paramref name="cmd"/>.</summary>
    public int Sent(string cmd) => Control.Sent.Count(r => r.Cmd == cmd);

    public ValueTask DisposeAsync() => Monitor.DisposeAsync();
}
