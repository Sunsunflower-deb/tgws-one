using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.VisualTree;
using TgwsOne.ViewModels;
using TgwsOne.Views;

namespace TgwsOne.Tests;

public class MainWindowTests
{
    private static readonly string[] Titles = ["Главная", "Обход DPI", "Диагностика", "Журналы", "Настройки"];

    [AvaloniaFact]
    public void MainWindow_ShowsFiveSidebarItems_AndSwitchesPages()
    {
        var vm = MainWindowViewModel.CreateShell();
        var window = new MainWindow { DataContext = vm };
        window.Show();

        var sidebar = window.FindControl<ListBox>("Sidebar")!;
        var host = window.FindControl<ContentControl>("PageHost")!;
        Assert.Equal(5, sidebar.ItemCount);
        Assert.Equal(Titles, vm.Pages.Select(p => p.Title));
        Assert.Equal(0, sidebar.SelectedIndex);
        Assert.Same(vm.Pages[0], host.Content);

        sidebar.SelectedIndex = 2;

        Assert.Equal("Диагностика", vm.SelectedPage.Title);
        Assert.Same(vm.Pages[2], host.Content);
        // The placeholder page is really drawn, with its own title.
        Assert.Contains(host.GetVisualDescendants().OfType<TextBlock>(), t => t.Text == "Диагностика");
    }

    [AvaloniaFact]
    public void MainWindow_SidebarItemsCarryTheirTitles()
    {
        var window = new MainWindow { DataContext = MainWindowViewModel.CreateShell() };
        window.Show();

        var texts = window.FindControl<ListBox>("Sidebar")!.GetVisualDescendants().OfType<TextBlock>().Select(t => t.Text);

        Assert.Equal(Titles, texts);
    }

    [AvaloniaFact]
    public void MainWindow_HasTheSpecSize()
    {
        var window = new MainWindow { DataContext = MainWindowViewModel.CreateShell() };

        Assert.Equal(820, window.Width);
        Assert.Equal(540, window.Height);
        Assert.Equal(720, window.MinWidth);
        Assert.Equal(480, window.MinHeight);
    }

    [Theory]
    [InlineData(new string[0], true)]
    [InlineData(new[] { "--tray" }, true)]
    [InlineData(new[] { "--start" }, false)]
    [InlineData(new[] { "--help" }, false)]
    [InlineData(new[] { "--zapret", "status" }, false)]
    [InlineData(new[] { "--service" }, false)]
    [InlineData(new[] { "--tray", "--start" }, false)]
    [InlineData(new[] { "--bogus" }, false)]
    public void Program_OnlyNoArgumentsOrTray_StartTheWindow(string[] args, bool window)
        => Assert.Equal(window, Program.IsUiLaunch(args));
}
