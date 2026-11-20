using TgwsOne.Views;

namespace TgwsOne.ViewModels;

/// <summary>The five pages of the sidebar, in the order they are listed: the value is the index into <c>MainWindowViewModel.Pages</c>.</summary>
internal enum AppPage { Home, Bypass, Diagnostics, Logs, Settings }

/// <summary>How a page asks the window to show another one.</summary>
internal interface INavigator
{
    /// <summary>Shows <paramref name="page"/>; with <paramref name="startCheck"/> (Diagnostics only) it also starts a run.</summary>
    void Show(AppPage page, bool startCheck = false);
}

/// <summary>What the sidebar calls each page and how it draws it.</summary>
internal static class AppPageInfo
{
    public static string Title(AppPage page) => page switch
    {
        AppPage.Home => "Главная",
        AppPage.Bypass => "Обход DPI",
        AppPage.Diagnostics => "Диагностика",
        AppPage.Logs => "Журналы",
        AppPage.Settings => "Настройки",
        _ => throw new ArgumentOutOfRangeException(nameof(page)),
    };

    public static string Icon(AppPage page) => page switch
    {
        AppPage.Home => IconPaths.Home,
        AppPage.Bypass => IconPaths.Shield,
        AppPage.Diagnostics => IconPaths.Activity,
        AppPage.Logs => IconPaths.List,
        AppPage.Settings => IconPaths.Sliders,
        _ => throw new ArgumentOutOfRangeException(nameof(page)),
    };
}
