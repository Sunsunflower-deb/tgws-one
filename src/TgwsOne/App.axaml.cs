using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Styling;
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
            desktop.MainWindow = new MainWindow { DataContext = MainWindowViewModel.CreateShell() };

        base.OnFrameworkInitializationCompleted();
    }
}
