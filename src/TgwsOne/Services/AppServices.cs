using Avalonia.Controls;
using TgwsOne.Core;
using TgwsOne.Core.Zapret;
using TgwsOne.ViewModels;

namespace TgwsOne.Services;

/// <summary>The real objects the window is built from: the only place that knows which implementation stands behind each interface.</summary>
internal sealed class AppServices : IAsyncDisposable
{
    private AppServices(AppPaths paths, AppLog log, IProxyControl proxy, IControlClient control, StatusMonitor monitor, Notifier notifier,
                        IAppSettings settings, ILauncher launcher, IClipboardService clipboard, IDialogService dialogs)
    {
        Notifier = notifier;
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

    /// <summary>Tells the user about transitions while the window is not looking; the pages' start and stop go through <see cref="Proxy"/>, which marks them as the user's own.</summary>
    public Notifier Notifier { get; }
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
        var notifier = new Notifier(NotificationSinks.ForThisMachine(paths), new SystemClock());
        IProxyControl proxy = new UserActionProxyControl(new ProxyControl(new ProxyController(paths, log)), notifier);
        IControlClient control = new ControlClient(ServicePaths.Current);
        var monitor = new StatusMonitor(proxy, control, new SystemNetInfo());
        notifier.Attach(monitor);
        return new AppServices(paths, log, proxy, control, monitor, notifier, new IniAppSettings(paths, log), new ShellLauncher(),
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

    /// <summary>Saves and deletes custom strategies with administrator rights (pkexec / UAC).</summary>
    public IElevatedHelper Elevated { get; } = ElevatedHelper.ForThisMachine();

    /// <summary>
    /// The editor of the named custom strategy (its file is read now), or of a new one, which starts from a small working example
    /// so the first «Проверить» shows what a good one looks like.
    /// </summary>
    public CustomStrategyViewModel CreateCustomEditor(string? name)
    {
        ServicePaths service = ServicePaths.Current;
        OsKind os = OperatingSystem.IsWindows() ? OsKind.Windows : OsKind.Linux;
        ZapretBundle bundle = ZapretBundle.Locate(os, AppContext.BaseDirectory, Environment.GetEnvironmentVariable);
        var context = new ZapretContext(os, bundle, service.Lists, service.StateDir, service.StrategiesDir);

        string text = NewStrategyExample;
        if (name != null)
        {
            try { text = File.ReadAllText(Path.Combine(service.StrategiesDir, name + ".args")); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { text = $"# Не удалось прочитать файл стратегии: {ex.Message}\n"; }
        }
        return new CustomStrategyViewModel(name, text, context, new ProcessCommandRunner(TimeSpan.FromSeconds(30)), Elevated, Clipboard, Launcher);
    }

    private const string NewStrategyExample =
        "# По одному параметру в строке. Это пример: один шаг разрезания TLS-запроса.\n" +
        "--lua-desync=multisplit:pos=1\n";

    /// <summary>A «QR для телефона» window's view-model, from the saved settings and this machine's network as they are now.</summary>
    public QrViewModel CreateQr() => new(Settings, new SystemNetInfo(), Clipboard);

    /// <summary>The connection check, with the real network probes (scripted ones when the dev switch says so).</summary>
    public Diagnostics CreateDiagnostics()
    {
        if (Environment.GetEnvironmentVariable(ScriptedProbes.Variable) == "1")
        {
            var scripted = new ScriptedProbes(Control);
            return new Diagnostics(Proxy, Control, new SystemNetInfo(), Settings, scripted, scripted, scripted, () => File.Exists(Paths.Engine));
        }
        return new Diagnostics(Proxy, Control, new SystemNetInfo(), Settings, new SocketTcpProbe(), new SslTlsProbe(), new HttpDownloadProbe(),
                               () => File.Exists(Paths.Engine));
    }

    public ValueTask DisposeAsync() => Monitor.DisposeAsync();
}
