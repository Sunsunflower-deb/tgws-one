using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace TgwsOne.ViewModels;

/// <summary>The window: the sidebar's pages in order and the one shown.</summary>
internal sealed partial class MainWindowViewModel : ObservableObject, INavigator
{
    [ObservableProperty]
    private PageViewModel _selectedPage;

    /// <summary>The pages are built by <paramref name="createPages"/>, which is handed the navigator (this window) for those that move around.</summary>
    public MainWindowViewModel(Func<INavigator, IReadOnlyList<PageViewModel>> createPages)
    {
        Pages = createPages(this);
        ArgumentOutOfRangeException.ThrowIfNotEqual(Pages.Count, Enum.GetValues<AppPage>().Length);
        _selectedPage = Pages[0];
    }

    public MainWindowViewModel(IReadOnlyList<PageViewModel> pages) : this(_ => pages)
    {
    }

    public IReadOnlyList<PageViewModel> Pages { get; }

    /// <summary>What «Выход» does (set by the shell, which owns the lifetime: it stops the proxy and ends the application). Nothing until then.</summary>
    public Action? RequestQuit { get; set; }

    [RelayCommand]
    private void Quit() => RequestQuit?.Invoke();

    partial void OnSelectedPageChanged(PageViewModel? oldValue, PageViewModel newValue)
    {
        oldValue?.OnHidden();
        newValue.OnShown();
    }

    /// <summary>Moves to <paramref name="page"/> (one of <see cref="Pages"/>).</summary>
    public void Navigate(PageViewModel page) => SelectedPage = page;

    public void Show(AppPage page, bool startCheck = false)
    {
        SelectedPage = Pages[(int)page];
        if (startCheck && SelectedPage is DiagnosticsViewModel diagnostics) diagnostics.StartRun();
    }

    /// <summary>A stand-in for a page that has not been built yet.</summary>
    public static PageViewModel Placeholder(AppPage page) => new PlaceholderPageViewModel(AppPageInfo.Title(page), AppPageInfo.Icon(page));

    /// <summary>The five pages of the spec, every one a placeholder.</summary>
    public static MainWindowViewModel CreateShell() => new([.. Enum.GetValues<AppPage>().Select(Placeholder)]);
}
