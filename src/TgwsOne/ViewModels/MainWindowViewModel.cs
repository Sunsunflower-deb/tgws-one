using CommunityToolkit.Mvvm.ComponentModel;
using TgwsOne.Views;

namespace TgwsOne.ViewModels;

/// <summary>The window: the sidebar's pages in order and the one shown.</summary>
internal sealed partial class MainWindowViewModel : ObservableObject
{
    [ObservableProperty]
    private PageViewModel _selectedPage;

    public MainWindowViewModel(IReadOnlyList<PageViewModel> pages)
    {
        ArgumentOutOfRangeException.ThrowIfZero(pages.Count);
        Pages = pages;
        _selectedPage = pages[0];
    }

    public IReadOnlyList<PageViewModel> Pages { get; }

    /// <summary>Moves to <paramref name="page"/> (one of <see cref="Pages"/>).</summary>
    public void Navigate(PageViewModel page) => SelectedPage = page;

    /// <summary>The five pages of the spec, every one a placeholder for now.</summary>
    public static MainWindowViewModel CreateShell() => new(
    [
        new PlaceholderPageViewModel("Главная", IconPaths.Home),
        new PlaceholderPageViewModel("Обход DPI", IconPaths.Shield),
        new PlaceholderPageViewModel("Диагностика", IconPaths.Activity),
        new PlaceholderPageViewModel("Журналы", IconPaths.List),
        new PlaceholderPageViewModel("Настройки", IconPaths.Sliders),
    ]);
}
