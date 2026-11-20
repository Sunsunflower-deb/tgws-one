using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using TgwsOne.Core.Zapret;
using TgwsOne.ViewModels;
using TgwsOne.Views;

namespace TgwsOne.Tests;

/// <summary>Обход DPI drawn on the headless platform.</summary>
public class BypassViewTests
{
    private static BypassView Show(BypassRig rig)
    {
        var view = new BypassView { DataContext = rig.Bypass };
        new Window { Content = view, Width = 820, Height = 700 }.Show();
        Dispatcher.UIThread.RunJobs();
        return view;
    }

    private static T Find<T>(BypassView view, string name) where T : Control
        => view.FindControl<T>(name) ?? throw new InvalidOperationException($"no control named {name}");

    private static string[] Texts(Control root) => [.. root.GetVisualDescendants().OfType<TextBlock>().Select(t => t.Text ?? "")];

    [AvaloniaFact]
    public async Task TheAutoTable_IsDrawnWithItsRows()
    {
        await using var rig = new BypassRig();
        rig.Service.Auto.AddRange([new AutoHost("kws2.web.telegram.org", "split", 1), new AutoHost("kws4.web.telegram.org", "fake-tls", 0)]);
        await rig.OpenAsync();
        BypassView view = Show(rig);

        Assert.True(Find<Border>(view, "AutoPanel").IsVisible);
        string[] texts = Texts(Find<ItemsControl>(view, "AutoList"));
        Assert.Contains("kws2.web.telegram.org", texts);
        Assert.Contains("split", texts);
        Assert.Contains("1 сбой", texts);
        Assert.Contains("kws4.web.telegram.org", texts);
        Assert.Contains("fake-tls", texts);
        Assert.Contains("—", texts);
        Assert.False(Find<TextBlock>(view, "AutoEmptyText").IsVisible);
    }

    [AvaloniaFact]
    public async Task TheAutoTable_WithoutData_ShowsTheHint()
    {
        await using var rig = new BypassRig();
        await rig.OpenAsync();
        BypassView view = Show(rig);

        Assert.True(Find<TextBlock>(view, "AutoEmptyText").IsVisible);
        Assert.Equal(BypassViewModel.EmptyAutoText, Find<TextBlock>(view, "AutoEmptyText").Text);
    }

    [AvaloniaFact]
    public async Task TheAutoTable_IsGone_WhenAnotherStrategyIsChosen()
    {
        await using var rig = new BypassRig();
        rig.Service.Strategy = "split";
        await rig.OpenAsync();
        BypassView view = Show(rig);

        Assert.False(Find<Border>(view, "AutoPanel").IsVisible);
    }

    [AvaloniaFact]
    public async Task EveryStrategy_IsARadioButton_AndTheCurrentOneIsChecked()
    {
        await using var rig = new BypassRig();
        rig.Service.StrategyIds.Add("custom:my-isp");
        rig.Service.Strategy = "split-stealth";
        await rig.OpenAsync();
        BypassView view = Show(rig);

        var radios = Find<ItemsControl>(view, "StrategyList").GetVisualDescendants().OfType<RadioButton>().ToList();

        Assert.Equal(6, radios.Count);
        Assert.Equal(["split-stealth"], radios.Where(r => r.IsChecked == true).Select(r => r.Content));
        Assert.Contains(radios, r => (string?)r.Content == "my-isp (своя)");
    }

    [AvaloniaFact]
    public async Task ClickingARadioButton_ChoosesThatStrategy()
    {
        await using var rig = new BypassRig();
        await rig.OpenAsync();
        BypassView view = Show(rig);
        var radio = Find<ItemsControl>(view, "StrategyList").GetVisualDescendants().OfType<RadioButton>().Single(r => (string?)r.Content == "aggressive");

        radio.Command!.Execute(radio.CommandParameter);
        await Task.Delay(50, TestContext.Current.CancellationToken);
        Dispatcher.UIThread.RunJobs();

        Assert.Equal("aggressive", rig.Service.Strategy);
    }

    [AvaloniaFact]
    public async Task WithoutAService_ThePageIsDisabled_AndExplainsWhy()
    {
        await using var rig = new BypassRig();
        rig.Service.Unavailable = true;
        await rig.Monitor.RefreshNowAsync();
        BypassView view = Show(rig);

        Assert.True(Find<Border>(view, "UnavailablePanel").IsVisible);
        Assert.Contains("Обход DPI не установлен — нужен администратор", Texts(Find<Border>(view, "UnavailablePanel")));
        Assert.False(Find<StackPanel>(view, "ContentPanel").IsEffectivelyEnabled);
    }

    [AvaloniaFact]
    public async Task WithAService_ThePageIsUsable_AndHasNoWarning()
    {
        await using var rig = new BypassRig();
        await rig.OpenAsync();
        BypassView view = Show(rig);

        Assert.False(Find<Border>(view, "UnavailablePanel").IsVisible);
        Assert.True(Find<StackPanel>(view, "ContentPanel").IsEffectivelyEnabled);
    }

    [AvaloniaFact]
    public async Task TheExclusionsEditor_OpensOnEdit_ShowsInvalidEntries_AndClosesOnSave()
    {
        await using var rig = new BypassRig();
        rig.Service.Hosts.Add("sberbank.ru");
        await rig.OpenAsync();
        BypassView view = Show(rig);
        Assert.False(Find<StackPanel>(view, "ExclusionsEditor").IsVisible);
        Assert.Equal("sberbank.ru", Find<TextBlock>(view, "ExclusionsSummaryText").Text);

        Find<Button>(view, "EditExclusionsButton").Command!.Execute(null);
        Dispatcher.UIThread.RunJobs();
        Assert.True(Find<StackPanel>(view, "ExclusionsEditor").IsVisible);
        Assert.Equal("sberbank.ru", Find<TextBox>(view, "HostsBox").Text);

        Find<TextBox>(view, "HostsBox").Text = "bad!host";   // typed into the box: the binding carries it to the view-model
        await rig.Bypass.SaveExclusionsCommand.ExecuteAsync(null);
        Dispatcher.UIThread.RunJobs();
        Assert.True(Find<TextBlock>(view, "InvalidTextBlock").IsVisible);
        Assert.Contains("bad!host", Find<TextBlock>(view, "InvalidTextBlock").Text);

        Find<TextBox>(view, "HostsBox").Text = "ok.example.com";
        await rig.Bypass.SaveExclusionsCommand.ExecuteAsync(null);
        Dispatcher.UIThread.RunJobs();
        Assert.False(Find<StackPanel>(view, "ExclusionsEditor").IsVisible);
        Assert.Equal("ok.example.com", Find<TextBlock>(view, "ExclusionsSummaryText").Text);
    }

    [AvaloniaFact]
    public async Task ARefusal_IsShownUnderTheList()
    {
        await using var rig = new BypassRig();
        rig.Service.UseRefusal = "проверка параметров не прошла";
        await rig.OpenAsync();
        BypassView view = Show(rig);

        await rig.Bypass.Strategies.Single(s => s.Id == "split").ChooseCommand.ExecuteAsync(null);
        Dispatcher.UIThread.RunJobs();

        Assert.True(Find<TextBlock>(view, "ErrorTextBlock").IsVisible);
        Assert.Equal("проверка параметров не прошла", Find<TextBlock>(view, "ErrorTextBlock").Text);
    }
}
