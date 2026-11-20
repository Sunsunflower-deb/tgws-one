using CommunityToolkit.Mvvm.ComponentModel;

namespace TgwsOne.ViewModels;

/// <summary>One entry of the sidebar: what it is called, how it is drawn, and (in the derived class) what its page shows.</summary>
internal abstract class PageViewModel(string title, string iconPath) : ObservableObject
{
    public string Title { get; } = title;

    /// <summary>SVG path data on the 24×24 grid, see <c>IconPaths</c>.</summary>
    public string IconPath { get; } = iconPath;
}

/// <summary>Stands in for a page until its own task lands.</summary>
internal sealed class PlaceholderPageViewModel(string title, string iconPath) : PageViewModel(title, iconPath)
{
    public string Note => $"Страница «{Title}» появится в следующих шагах.";
}
