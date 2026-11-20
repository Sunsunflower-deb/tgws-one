using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Platform;
using Avalonia.Threading;
using Avalonia.Styling;
using TgwsOne.Core.Zapret;
using TgwsOne.Services;
using TgwsOne.ViewModels;
using TgwsOne.Views;

namespace TgwsOne;

internal sealed partial class App : Application
{
    /// <summary>Dev only: <c>TGWS_THEME=light|dark</c> forces the theme (for screenshots). Not a setting; the app follows the OS.</summary>
    private const string ThemeVariable = "TGWS_THEME";

    /// <summary>Set by <c>Program</c> before the app starts: the window is shown when a later launch asks for it. Null under the tests and the previewer.</summary>
    internal static SingleInstance? Instance { get; set; }

    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        RequestedThemeVariant = Environment.GetEnvironmentVariable(ThemeVariable) switch
        {
            "light" => ThemeVariant.Light,
            "dark" => ThemeVariant.Dark,
            _ => ThemeVariant.Default,
        };

        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            // --tray (the autostart's start): no window, unless there is no tray to find it in; connect by itself.
            bool quiet = desktop.Args is ["--tray"];

            MainWindow? window = null;
            AppServices services = AppServices.Create(() => window);
            IAutostart autostart = Autostart.ForThisMachine(Environment.ProcessPath ?? "");
            Pages pages = CreateMainViewModel(services, autostart);
            window = new MainWindow { DataContext = pages.Main };

            // The × only hides the window; the application ends with «Выход» (and with the session), which stops the proxy first.
            desktop.ShutdownMode = ShutdownMode.OnExplicitShutdown;
            bool trayAvailable = TrayIsAvailable();
            var shell = new AppShell(desktop, window, services, new CloseBehavior(trayAvailable, services.Settings, services.Notifier));
            pages.Main.RequestQuit = shell.Quit;

            TrayService? tray = trayAvailable
                ? new TrayService(this, new TrayViewModel(pages.Home, pages.Bypass, services.Monitor, shell.Show, shell.Quit), OpenAppIcon, shell.Show)
                : null;
            desktop.Exit += (_, _) =>
            {
                tray?.Dispose();
                shell.Dispose();
                services.DisposeAsync().AsTask().GetAwaiter().GetResult();
            };

            // A later launch asked for the window.
            if (Instance is { } instance)
                instance.ShowRequested += () => Dispatcher.UIThread.Post(shell.Show);

            if (quiet)
            {
                if (!trayAvailable)
                {
                    window.WindowState = WindowState.Minimized;   // nowhere to hide: a minimized window, where it can be found
                    desktop.MainWindow = window;
                }
                Dispatcher.UIThread.Post(() => _ = AutoConnectAsync(pages.Actions, services.Notifier));
            }
            else
            {
                desktop.MainWindow = window;
                // First run: the wizard, over the window.
                if (!services.Settings.FirstRunDone)
                    window.Opened += async (_, _) => await ShowWizardAsync(window, services, pages.Actions, autostart);
            }
        }

        base.OnFrameworkInitializationCompleted();
    }

    private static async Task ShowWizardAsync(MainWindow owner, AppServices s, IDiagnosticsActions actions, IAutostart autostart)
    {
        var wizard = new WizardViewModel(actions, s.Monitor, s.CreateDiagnostics(), s.Settings, autostart);
        await new WizardWindow { DataContext = wizard }.ShowDialog(owner);
    }

    /// <summary>The window's view-model with the pages the tray and the wizard borrow from: their commands are the very ones on the pages.</summary>
    private sealed record Pages(MainWindowViewModel Main, HomeViewModel Home, BypassViewModel Bypass, IDiagnosticsActions Actions);

    private static Pages CreateMainViewModel(AppServices s, IAutostart autostart)
    {
        HomeViewModel? home = null;
        BypassViewModel? bypass = null;
        IDiagnosticsActions? actions = null;
        var main = new MainWindowViewModel(nav =>
        {
            home = new HomeViewModel(s.Proxy, s.Control, s.Monitor, s.Settings, s.Launcher, s.Clipboard, s.Dialogs, nav, s.CreateQr);
            bypass = new BypassViewModel(s.Control, s.Monitor, s.Dialogs, s.CreateFinder, s.CreateCustomEditor, s.Elevated, s.Clipboard);
            actions = new DiagnosticsActions(home, bypass, s.Control, s.Monitor);
            return
            [
                home,
                bypass,
                new DiagnosticsViewModel(s.CreateDiagnostics(), nav, actions),
                new LogsViewModel(LogsViewModel.DefaultTabs(s.Paths, ServicePaths.Current), new DispatcherTicker(), s.Clipboard, s.Launcher),
                new SettingsViewModel(s.Settings, s.Proxy, s.Dialogs, s.Clipboard, new SystemNetInfo(), autostart, home.RestartProxyAsync, s.CreateQr),
            ];
        });
        return new Pages(main, home!, bypass!, actions!);
    }

    private static Stream OpenAppIcon() => AssetLoader.Open(new Uri("avares://TgwsOne/Assets/app.ico"));

    // A tray icon with nothing to show it is worse than none: the window would hide and never be found again.
    private static bool TrayIsAvailable()
    {
        try
        {
            return Task.Run(() => TrayAvailability.CheckAsync()).WaitAsync(TimeSpan.FromSeconds(2)).GetAwaiter().GetResult();
        }
        catch (Exception ex) when (ex is TimeoutException or OperationCanceledException)
        {
            return false;
        }
    }

    /// <summary>The autostart's start connects by itself; when it does not come up there is no window to say so, so a notification does.</summary>
    private static async Task AutoConnectAsync(IDiagnosticsActions actions, Notifier notifier)
    {
        try
        {
            if (await actions.ConnectAsync() is { } failure)
                notifier.Notify(NoticeKind.StartFailed, Notifier.Title, Notifier.StartFailedText(failure));
        }
        catch (Exception ex)
        {
            // Nobody is waiting on this: whatever went wrong, it is a notification and not a crash.
            notifier.Notify(NoticeKind.StartFailed, Notifier.Title, Notifier.StartFailedText(ex.Message));
        }
    }
}
