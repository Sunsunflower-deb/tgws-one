using System.Text;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using TgwsOne.Core;
using TgwsOne.Core.Zapret;
using TgwsOne.ViewModels;
using TgwsOne.Views;

namespace TgwsOne.Tests;

/// <summary>Журналы drawn on the headless platform.</summary>
public class LogsViewTests
{
    private sealed class Rig : IDisposable
    {
        private readonly TempDir _tmp = new();

        public AppPaths App { get; }
        public ServicePaths Service { get; }
        public ManualTicker Ticker { get; } = new();
        public LogsViewModel Vm { get; }
        public LogsView View { get; private set; } = null!;

        public Rig()
        {
            App = AppPaths.For(OsKind.Linux, n => n == "TGWS_DATA_DIR" ? _tmp.Combine("data") : null, "/opt/tgwsone", "/home/nobody");
            Service = ServicePaths.For(OsKind.Linux, n => n == "TGWS_SERVICE_ROOT" ? _tmp.Combine("service") : null);
            Vm = new LogsViewModel(LogsViewModel.DefaultTabs(App, Service), Ticker, new FakeClipboard(), new FakeLauncher());
        }

        public void Open()
        {
            Vm.OnShown();
            View = new LogsView { DataContext = Vm };
            new Window { Content = View, Width = 820, Height = 600 }.Show();
            Dispatcher.UIThread.RunJobs();
        }

        public static void Put(string path, string text)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.AppendAllText(path, text, new UTF8Encoding(false));
        }

        public LogTextView Text => View.GetVisualDescendants().OfType<LogTextView>().Single();

        public void Dispose() => _tmp.Dispose();
    }

    [AvaloniaFact]
    public void TheThreeTabsAreThere_AndTheFirstShowsItsText()
    {
        using var rig = new Rig();
        Rig.Put(rig.App.AppLogFile, "из приложения\n");

        rig.Open();

        var tabs = rig.View.FindControl<TabControl>("TabsControl")!;
        Assert.Equal(3, tabs.ItemCount);
        Assert.Equal(["Приложение", "Прокси", "Обход DPI"], tabs.GetVisualDescendants().OfType<TabItem>().Select(t => t.GetVisualDescendants().OfType<TextBlock>().First().Text));
        Assert.Equal("из приложения", rig.Text.Text);
    }

    [AvaloniaFact]
    public void ChoosingATab_ShowsThatLog()
    {
        using var rig = new Rig();
        Rig.Put(rig.App.AppLogFile, "из приложения\n");
        Rig.Put(Path.Combine(rig.Service.LogsDir, "service.log"), "служба запущена\n");
        rig.Open();

        rig.View.FindControl<TabControl>("TabsControl")!.SelectedIndex = 2;
        Dispatcher.UIThread.RunJobs();

        Assert.Equal(rig.Vm.Tabs[2], rig.Vm.SelectedTab);
        Assert.Contains("=== service.log ===\nслужба запущена", rig.Text.Text);
    }

    [AvaloniaFact]
    public void AMissingLog_ShowsAMessage_NotAnEmptyBox()
    {
        using var rig = new Rig();

        rig.Open();

        Assert.False(rig.Text.IsVisible);
        string[] texts = [.. rig.View.GetVisualDescendants().OfType<TextBlock>().Where(t => t.IsEffectivelyVisible).Select(t => t.Text ?? "")];
        Assert.Contains(texts, t => t.StartsWith("Журнала пока нет"));
    }

    [AvaloniaFact]
    public void TheButtonsAreThere_AndFollowingIsOnByDefault()
    {
        using var rig = new Rig();
        rig.Open();

        Assert.Equal("Копировать", rig.View.FindControl<Button>("CopyButton")!.Content);
        Assert.Equal("Открыть папку", rig.View.FindControl<Button>("OpenFolderButton")!.Content);
        Assert.True(rig.View.FindControl<CheckBox>("FollowCheck")!.IsChecked);
    }

    // ----- the text control: following the end -----

    private static LogTextView Host(out Window window)
    {
        var view = new LogTextView { Height = 200, Width = 400 };
        window = new Window { Content = view, Width = 420, Height = 220 };
        window.Show();
        Dispatcher.UIThread.RunJobs();
        return view;
    }

    private static string Lines(int from, int to) => string.Join('\n', Enumerable.Range(from, to - from + 1).Select(i => $"line {i}"));

    private static void Settle(Window window)
    {
        window.UpdateLayout();
        Dispatcher.UIThread.RunJobs();
        window.UpdateLayout();
    }

    [AvaloniaFact]
    public void NewText_StartsAtTheEnd_WhenFollowing()
    {
        LogTextView view = Host(out Window window);

        view.Text = Lines(1, 200);
        Settle(window);

        var scroll = view.Scroller;
        Assert.True(scroll.Extent.Height > scroll.Viewport.Height);
        Assert.True(scroll.Offset.Y >= scroll.Extent.Height - scroll.Viewport.Height - 3);
    }

    [AvaloniaFact]
    public void WhileTheReaderIsAtTheEnd_MoreTextKeepsThemThere()
    {
        LogTextView view = Host(out Window window);
        view.Text = Lines(1, 200);
        Settle(window);

        view.Text = Lines(1, 260);
        Settle(window);

        var scroll = view.Scroller;
        Assert.True(scroll.Offset.Y >= scroll.Extent.Height - scroll.Viewport.Height - 3);
    }

    [AvaloniaFact]
    public void AReaderWhoScrolledUp_IsNotPulledBackDown()
    {
        LogTextView view = Host(out Window window);
        view.Text = Lines(1, 200);
        Settle(window);
        view.Scroller.Offset = new Vector(0, 100);   // reading something older
        Settle(window);

        view.Text = Lines(1, 260);
        Settle(window);

        Assert.Equal(100, view.Scroller.Offset.Y, 1);
    }

    [AvaloniaFact]
    public void NotFollowing_NeverMovesTheView()
    {
        LogTextView view = Host(out Window window);
        view.FollowEnd = false;
        view.Text = Lines(1, 200);
        Settle(window);
        Assert.Equal(0, view.Scroller.Offset.Y, 1);

        view.Text = Lines(1, 260);
        Settle(window);

        Assert.Equal(0, view.Scroller.Offset.Y, 1);
    }

    [AvaloniaFact]
    public void TurningFollowingOn_JumpsToTheEnd()
    {
        LogTextView view = Host(out Window window);
        view.FollowEnd = false;
        view.Text = Lines(1, 200);
        Settle(window);

        view.FollowEnd = true;
        Settle(window);

        var scroll = view.Scroller;
        Assert.True(scroll.Offset.Y >= scroll.Extent.Height - scroll.Viewport.Height - 3);
    }

    [AvaloniaFact]
    public void TheText_IsMonospace_AndCanBeSelected()
    {
        LogTextView view = Host(out Window window);
        view.Text = "abc";
        Settle(window);

        var block = view.GetVisualDescendants().OfType<SelectableTextBlock>().Single();

        Assert.Contains("Mono", block.FontFamily.Name + block.FontFamily.FamilyNames.ToString(), StringComparison.OrdinalIgnoreCase);
        Assert.Equal("abc", block.Text);
    }
}
