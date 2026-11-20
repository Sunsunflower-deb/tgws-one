using Avalonia.Controls;
using TgwsOne.Core;
using TgwsOne.Core.Zapret;
using TgwsOne.ViewModels;

namespace TgwsOne.Services;

/// <summary>The real objects the window is built from: the only place that knows which implementation stands behind each interface.</summary>
internal sealed class AppServices : IAsyncDisposable
{
    private AppServices(AppPaths paths, AppLog log, IProxyControl proxy, IControlClient control, StatusMonitor monitor,
                        IAppSettings settings, ILauncher launcher, IClipboardService clipboard, IDialogService dialogs)
    {
        Paths = paths;
        Log = log;
        Proxy = proxy;
        Control = control;
        Monitor = monitor;
        Settings = settings;
        Launcher = launcher;
        Clipboard = clipboard;
        Dialogs = dialogs;
    }

    public AppPaths Paths { get; }
    public AppLog Log { get; }
    public IProxyControl Proxy { get; }
    public IControlClient Control { get; }
    public StatusMonitor Monitor { get; }
    public IAppSettings Settings { get; }
    public ILauncher Launcher { get; }
    public IClipboardService Clipboard { get; }
    public IDialogService Dialogs { get; }

    /// <summary>Must be called on the UI thread: the monitor delivers its snapshots to the thread it was created on.</summary>
    /// <param name="owner">The main window, for the dialogs and the clipboard (it does not exist yet when this runs).</param>
    public static AppServices Create(Func<Window?> owner)
    {
        AppPaths paths = AppPaths.Current;
        var log = new AppLog(paths.AppLogFile);
        IProxyControl proxy = new ProxyControl(new ProxyController(paths, log));
        IControlClient control = new ControlClient(ServicePaths.Current);
        var monitor = new StatusMonitor(proxy, control, new SystemNetInfo());
        return new AppServices(paths, log, proxy, control, monitor, new IniAppSettings(paths, log), new ShellLauncher(),
                               new AvaloniaClipboardService(owner), new AvaloniaDialogService(owner));
    }

    /// <summary>A strategy finder with the real network probes (scripted ones when the dev switch says so); a new one for each run.</summary>
    public FinderViewModel CreateFinder()
    {
        if (Environment.GetEnvironmentVariable(ScriptedProbes.Variable) == "1")
        {
            var scripted = new ScriptedProbes(Control);
            return new FinderViewModel(new StrategyFinder(Control, scripted, scripted));
        }
        return new FinderViewModel(new StrategyFinder(Control, new SslTlsProbe(), new HttpDownloadProbe()));
    }

    public ValueTask DisposeAsync() => Monitor.DisposeAsync();
}
