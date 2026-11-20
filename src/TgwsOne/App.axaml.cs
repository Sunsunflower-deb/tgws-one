using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Styling;
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
            desktop.MainWindow = new MainWindow { DataContext = CreateMainViewModel(services) };
            desktop.Exit += (_, _) => services.DisposeAsync().AsTask().GetAwaiter().GetResult();
        }

        base.OnFrameworkInitializationCompleted();
    }

    private static MainWindowViewModel CreateMainViewModel(AppServices s) => new(nav =>
    [
        new HomeViewModel(s.Proxy, s.Control, s.Monitor, s.Settings, s.Launcher, s.Clipboard, s.Dialogs, nav),
        new BypassViewModel(s.Control, s.Monitor),
        MainWindowViewModel.Placeholder(AppPage.Diagnostics),
        MainWindowViewModel.Placeholder(AppPage.Logs),
        MainWindowViewModel.Placeholder(AppPage.Settings),
    ]);
}
