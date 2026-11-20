using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
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

        // The headless screen is a roomy one, so nothing is cut down.
        Assert.Equal(880, window.Width);
        Assert.Equal(730, window.Height);
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

    // ----- Выход -----

    [Fact]
    public void QuitCommand_DoesNothingUntilTheShellSaysWhatQuittingIs_ThenCallsIt()
    {
        var vm = MainWindowViewModel.CreateShell();
        vm.QuitCommand.Execute(null);   // no shell yet: not an error

        int quits = 0;
        vm.RequestQuit = () => quits++;
        vm.QuitCommand.Execute(null);

        Assert.Equal(1, quits);
    }

    [AvaloniaFact]
    public void TheSidebarHasAnExitButton_BelowTheSections_AndItQuits()
    {
        var vm = MainWindowViewModel.CreateShell();
        int quits = 0;
        vm.RequestQuit = () => quits++;
        var window = new MainWindow { DataContext = vm };
        window.Show();

        var quit = window.FindControl<Button>("QuitButton")!;
        var sidebar = window.FindControl<ListBox>("Sidebar")!;

        Assert.True(quit.IsVisible);
        Assert.Contains(quit.GetVisualDescendants().OfType<TextBlock>(), t => t.Text == "Выход");
        Assert.True(quit.TranslatePoint(default, window)!.Value.Y > sidebar.TranslatePoint(default, window)!.Value.Y);
        quit.Command!.Execute(null);
        Assert.Equal(1, quits);
    }

    [AvaloniaFact]
    public void CtrlQ_Quits()
    {
        var vm = MainWindowViewModel.CreateShell();
        int quits = 0;
        vm.RequestQuit = () => quits++;
        var window = new MainWindow { DataContext = vm };
        window.Show();

        window.KeyPress(Avalonia.Input.Key.Q, Avalonia.Input.RawInputModifiers.Control, Avalonia.Input.PhysicalKey.Q, "q");

        Assert.Equal(1, quits);
    }

    [AvaloniaFact]
    public void QWithoutCtrl_DoesNotQuit()
    {
        var vm = MainWindowMakeForQuit(out Func<int> quits);
        var window = new MainWindow { DataContext = vm };
        window.Show();

        window.KeyPress(Avalonia.Input.Key.Q, Avalonia.Input.RawInputModifiers.None, Avalonia.Input.PhysicalKey.Q, "q");

        Assert.Equal(0, quits());
    }

    private static MainWindowViewModel MainWindowMakeForQuit(out Func<int> quits)
    {
        var vm = MainWindowViewModel.CreateShell();
        int count = 0;
        vm.RequestQuit = () => count++;
        quits = () => count;
        return vm;
    }
}

