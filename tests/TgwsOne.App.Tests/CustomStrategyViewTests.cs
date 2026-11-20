using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using TgwsOne.Services;
using TgwsOne.ViewModels;
using TgwsOne.Views;

namespace TgwsOne.Tests;

/// <summary>The strategy editor's window and the «Свои стратегии» section, drawn on the headless platform.</summary>
public class CustomStrategyViewTests
{
    private const string Valid = "--lua-desync=multisplit:pos=1\n";

    private static (CustomStrategyDialog Dialog, EditorKit Kit, CustomStrategyViewModel Vm) Show(string? name = null, string text = Valid)
    {
        var kit = new EditorKit();
        CustomStrategyViewModel vm = kit.Editor(name, text);
        var dialog = new CustomStrategyDialog { DataContext = vm };
        dialog.Show();
        Dispatcher.UIThread.RunJobs();
        return (dialog, kit, vm);
    }

    private static T Find<T>(CustomStrategyDialog d, string name) where T : Control
        => d.FindControl<T>(name) ?? throw new InvalidOperationException($"no control named {name}");

    // ----- the editor window -----

    [AvaloniaFact]
    public void ANewStrategy_ShowsAnEditableName_TheText_AndTheButtons()
    {
        (CustomStrategyDialog dialog, EditorKit kit, _) = Show();
        using (kit)
        {
            Assert.Equal("Новая своя стратегия", Find<TextBlock>(dialog, "TitleText").Text);
            Assert.True(Find<TextBox>(dialog, "NameBox").IsEffectivelyEnabled);
            Assert.Equal(Valid, Find<TextBox>(dialog, "ParamsBox").Text);
            Assert.Equal("Проверить", Find<Button>(dialog, "CheckButton").Content);
            Assert.Equal("Сохранить (нужны права администратора)", Find<Button>(dialog, "SaveButton").Content);
            Assert.Equal("Отмена", Find<Button>(dialog, "CancelButton").Content);
            Assert.Equal("Справка по параметрам", Find<Button>(dialog, "HelpButton").Content);
        }
    }

    [AvaloniaFact]
    public void AnExistingStrategy_HasItsNameFixed()
    {
        (CustomStrategyDialog dialog, EditorKit kit, _) = Show("my-isp");
        using (kit)
        {
            Assert.Equal("Своя стратегия «my-isp»", Find<TextBlock>(dialog, "TitleText").Text);
            Assert.Equal("my-isp", Find<TextBox>(dialog, "NameBox").Text);
            Assert.False(Find<TextBox>(dialog, "NameBox").IsEffectivelyEnabled);
        }
    }

    [AvaloniaFact]
    public async Task AnErrorInTheName_AppearsUnderTheName()
    {
        (CustomStrategyDialog dialog, EditorKit kit, CustomStrategyViewModel vm) = Show();
        using (kit)
        {
            Assert.False(Find<TextBlock>(dialog, "NameErrorText").IsVisible);
            Find<TextBox>(dialog, "NameBox").Text = "Bad Name";

            await vm.SaveCommand.ExecuteAsync(null);
            Dispatcher.UIThread.RunJobs();

            Assert.True(Find<TextBlock>(dialog, "NameErrorText").IsVisible);
            Assert.Equal(CustomStrategyViewModel.NameRule, Find<TextBlock>(dialog, "NameErrorText").Text);
            Assert.False(Find<TextBlock>(dialog, "TextErrorText").IsVisible);
        }
    }

    [AvaloniaFact]
    public async Task AnErrorInTheText_AppearsUnderTheText()
    {
        (CustomStrategyDialog dialog, EditorKit kit, CustomStrategyViewModel vm) = Show("x", "--qnum=7\n");
        using (kit)
        {
            await vm.CheckCommand.ExecuteAsync(null);
            Dispatcher.UIThread.RunJobs();

            Assert.True(Find<TextBlock>(dialog, "TextErrorText").IsVisible);
            Assert.Equal("недопустимый параметр в стратегии: --qnum", Find<TextBlock>(dialog, "TextErrorText").Text);
            Assert.False(Find<TextBlock>(dialog, "NameErrorText").IsVisible);
        }
    }

    [AvaloniaFact]
    public async Task TheCheckOutput_AppearsInItsOwnBox()
    {
        (CustomStrategyDialog dialog, EditorKit kit, CustomStrategyViewModel vm) = Show("x");
        using (kit)
        {
            kit.Runner.Output = "command line parameters verified";
            Assert.False(Find<Border>(dialog, "CheckPanel").IsVisible);

            await vm.CheckCommand.ExecuteAsync(null);
            Dispatcher.UIThread.RunJobs();

            Assert.True(Find<Border>(dialog, "CheckPanel").IsVisible);
            Assert.Equal("Проверка пройдена.\ncommand line parameters verified", Find<SelectableTextBlock>(dialog, "CheckOutputText").Text);
        }
    }

    [AvaloniaFact]
    public async Task WhenNoPasswordCanBeAskedFor_TheCommandIsShownWithACopyButton()
    {
        (CustomStrategyDialog dialog, EditorKit kit, CustomStrategyViewModel vm) = Show("x");
        using (kit)
        {
            kit.Helper.SaveResult = new AdminOutcome(false, "Не нашёл pkexec: запросить права администратора не получится.", "sudo /usr/lib/tgwsone/tgwsone --admin save-strategy x /tmp/x.args");
            Assert.False(Find<Border>(dialog, "FallbackPanel").IsVisible);

            await vm.SaveCommand.ExecuteAsync(null);
            Dispatcher.UIThread.RunJobs();

            Assert.True(Find<Border>(dialog, "FallbackPanel").IsVisible);
            Assert.Equal("Не нашёл pkexec: запросить права администратора не получится.", Find<TextBlock>(dialog, "FallbackProblemText").Text);
            Assert.Equal("sudo /usr/lib/tgwsone/tgwsone --admin save-strategy x /tmp/x.args", Find<SelectableTextBlock>(dialog, "FallbackText").Text);
            Assert.Equal("Копировать", Find<Button>(dialog, "CopyFallbackButton").Content);
            Assert.False(Find<TextBlock>(dialog, "ProblemText").IsVisible);   // told once, in the panel
        }
    }

    [AvaloniaFact]
    public async Task ARefusal_IsShownOnItsOwnLine_WithNoCommandPanel()
    {
        (CustomStrategyDialog dialog, EditorKit kit, CustomStrategyViewModel vm) = Show("x");
        using (kit)
        {
            kit.Helper.SaveResult = new AdminOutcome(false, "файл /srv/x.bin недоступен для чтения всем (chmod o+r)");

            await vm.SaveCommand.ExecuteAsync(null);
            Dispatcher.UIThread.RunJobs();

            Assert.True(Find<TextBlock>(dialog, "ProblemText").IsVisible);
            Assert.Equal("файл /srv/x.bin недоступен для чтения всем (chmod o+r)", Find<TextBlock>(dialog, "ProblemText").Text);
            Assert.False(Find<Border>(dialog, "FallbackPanel").IsVisible);
        }
    }

    [AvaloniaFact]
    public async Task ASavedStrategy_ClosesTheWindow()
    {
        (CustomStrategyDialog dialog, EditorKit kit, CustomStrategyViewModel vm) = Show("x");
        using (kit)
        {
            await vm.SaveCommand.ExecuteAsync(null);
            Dispatcher.UIThread.RunJobs();

            Assert.False(dialog.IsVisible);
            Assert.True(vm.Saved);
        }
    }

    [AvaloniaFact]
    public void Cancel_ClosesTheWindow()
    {
        (CustomStrategyDialog dialog, EditorKit kit, _) = Show("x");
        using (kit)
        {
            Find<Button>(dialog, "CancelButton").Command!.Execute(null);
            Dispatcher.UIThread.RunJobs();

            Assert.False(dialog.IsVisible);
        }
    }

    // ----- the section on the page -----

    private static BypassView ShowPage(BypassRig rig)
    {
        var view = new BypassView { DataContext = rig.Bypass };
        new Window { Content = view, Width = 820, Height = 1100 }.Show();
        Dispatcher.UIThread.RunJobs();
        return view;
    }

    private static T FindOn<T>(BypassView v, string name) where T : Control
        => v.FindControl<T>(name) ?? throw new InvalidOperationException($"no control named {name}");

    [AvaloniaFact]
    public async Task TheSection_ListsEachStrategyWithItsButtons()
    {
        await using var rig = new BypassRig();
        rig.Service.StrategyIds.AddRange(["custom:my-isp", "custom:other"]);
        await rig.OpenAsync();
        BypassView view = ShowPage(rig);

        var list = FindOn<ItemsControl>(view, "CustomList");
        string[] texts = [.. list.GetVisualDescendants().OfType<TextBlock>().Select(t => t.Text ?? "")];
        Assert.Contains("my-isp", texts);
        Assert.Contains("other", texts);
        var buttons = list.GetVisualDescendants().OfType<Button>().Select(b => b.Content).ToList();
        Assert.Equal(["Изменить", "Удалить", "Изменить", "Удалить"], buttons);
        Assert.False(FindOn<TextBlock>(view, "NoCustomText").IsVisible);
        Assert.Equal("Создать", FindOn<Button>(view, "CreateCustomButton").Content);
    }

    [AvaloniaFact]
    public async Task WithNoneOfTheirOwn_TheSectionSaysSo()
    {
        await using var rig = new BypassRig();
        await rig.OpenAsync();
        BypassView view = ShowPage(rig);

        Assert.True(FindOn<TextBlock>(view, "NoCustomText").IsVisible);
        Assert.Equal("Своих стратегий пока нет.", FindOn<TextBlock>(view, "NoCustomText").Text);
        Assert.Empty(FindOn<ItemsControl>(view, "CustomList").GetVisualDescendants().OfType<Button>());
    }

    [AvaloniaFact]
    public async Task ANoticeAndAFallbackCommand_AreShownUnderTheList()
    {
        await using var rig = new BypassRig();
        rig.Service.StrategyIds.Add("custom:a");
        await rig.OpenAsync();
        BypassView view = ShowPage(rig);
        rig.Helper.DeleteResult = new AdminOutcome(false, "Не нашёл pkexec.", "sudo /usr/lib/tgwsone/tgwsone --admin delete-strategy a");

        await rig.Bypass.CustomStrategies.Single().DeleteCommand.ExecuteAsync(null);
        Dispatcher.UIThread.RunJobs();

        Assert.True(FindOn<TextBlock>(view, "CustomNoticeText").IsVisible);
        Assert.Equal("Не нашёл pkexec.", FindOn<TextBlock>(view, "CustomNoticeText").Text);
        Assert.True(FindOn<StackPanel>(view, "CustomFallbackPanel").IsVisible);
        Assert.Equal("sudo /usr/lib/tgwsone/tgwsone --admin delete-strategy a", FindOn<SelectableTextBlock>(view, "CustomFallbackText").Text);
    }
}
