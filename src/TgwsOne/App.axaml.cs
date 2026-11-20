using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
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
            AppServices services = AppServices.Create(() => desktop.MainWindow);
            IAutostart autostart = Autostart.ForThisMachine(Environment.ProcessPath ?? "");
            (MainWindowViewModel main, IDiagnosticsActions actions) = CreateMainViewModel(services, autostart);
            var window = new MainWindow { DataContext = main };
            desktop.MainWindow = window;
            desktop.Exit += (_, _) => services.DisposeAsync().AsTask().GetAwaiter().GetResult();

            // First run: the wizard, over the window (not for the autostart's --tray start, which is meant to be quiet).
            if (!services.Settings.FirstRunDone && desktop.Args is not ["--tray"])
                window.Opened += async (_, _) => await ShowWizardAsync(window, services, actions, autostart);
        }

        base.OnFrameworkInitializationCompleted();
    }

    private static async Task ShowWizardAsync(MainWindow owner, AppServices s, IDiagnosticsActions actions, IAutostart autostart)
    {
        var wizard = new WizardViewModel(actions, s.Monitor, s.CreateDiagnostics(), s.Settings, autostart);
        await new WizardWindow { DataContext = wizard }.ShowDialog(owner);
    }

    /// <summary>The window's view-model, and the page actions the wizard borrows (so «Подключить» there is the one on Главная).</summary>
    private static (MainWindowViewModel Main, IDiagnosticsActions Actions) CreateMainViewModel(AppServices s, IAutostart autostart)
    {
        IDiagnosticsActions? actions = null;
        var main = new MainWindowViewModel(nav =>
        {
            var home = new HomeViewModel(s.Proxy, s.Control, s.Monitor, s.Settings, s.Launcher, s.Clipboard, s.Dialogs, nav, s.CreateQr);
            var bypass = new BypassViewModel(s.Control, s.Monitor, s.Dialogs, s.CreateFinder, s.CreateCustomEditor, s.Elevated, s.Clipboard);
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
        return (main, actions ?? throw new InvalidOperationException("the page factory did not run"));
    }
}
