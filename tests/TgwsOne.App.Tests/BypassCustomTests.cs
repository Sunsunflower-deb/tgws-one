using TgwsOne.Core.Zapret;
using TgwsOne.Services;
using TgwsOne.ViewModels;

namespace TgwsOne.Tests;

/// <summary>«Свои стратегии (админ)» on the Обход DPI page: the list, and creating, editing and deleting.</summary>
public class BypassCustomTests
{
    private const string Valid = "--lua-desync=multisplit:pos=1\n";

    // ----- the list -----

    [Fact]
    public async Task TheCustomStrategies_AreListedByName_AndAlsoOfferedAsStrategies()
    {
        await using var rig = new BypassRig();
        rig.Service.StrategyIds.AddRange(["custom:my-isp", "custom:other"]);

        await rig.OpenAsync();

        Assert.Equal(["my-isp", "other"], rig.Bypass.CustomStrategies.Select(c => c.Name));
        Assert.Equal(["custom:my-isp", "custom:other"], rig.Bypass.CustomStrategies.Select(c => c.Id));
        Assert.True(rig.Bypass.HasCustom);
        Assert.Contains(rig.Bypass.Strategies, s => s.Id == "custom:my-isp" && s.Name == "my-isp (своя)");
    }

    [Fact]
    public async Task WithNoCustomStrategy_TheListIsEmpty()
    {
        await using var rig = new BypassRig();

        await rig.OpenAsync();

        Assert.Empty(rig.Bypass.CustomStrategies);
        Assert.False(rig.Bypass.HasCustom);
    }

    // ----- creating and editing -----

    [Fact]
    public async Task Create_OpensAnEmptyEditor_AndAShowsTheNewStrategyOnceSaved()
    {
        await using var rig = new BypassRig();
        await rig.OpenAsync();
        rig.Dialogs.WhileEditorOpen = async editor =>
        {
            editor.Name = "my-isp";
            editor.Text = Valid;
            await editor.SaveCommand.ExecuteAsync(null);
        };

        await rig.Bypass.CreateCustomCommand.ExecuteAsync(null);

        Assert.Equal([null], rig.EditorsOpenedFor);
        Assert.Equal([("my-isp", Valid)], rig.Helper.Saved);
        Assert.Equal(["my-isp"], rig.Bypass.CustomStrategies.Select(c => c.Name));
        Assert.Contains(rig.Bypass.Strategies, s => s.Id == "custom:my-isp");
        Assert.Equal("Стратегия «my-isp» сохранена.", rig.Bypass.CustomNotice);
        Assert.Equal(0, rig.Service.Count("use"));   // a new strategy is saved, not switched to
    }

    [Fact]
    public async Task Create_ThatIsCancelled_ChangesNothing()
    {
        await using var rig = new BypassRig();
        await rig.OpenAsync();
        rig.Dialogs.WhileEditorOpen = editor =>
        {
            editor.CancelCommand.Execute(null);
            return Task.CompletedTask;
        };

        await rig.Bypass.CreateCustomCommand.ExecuteAsync(null);

        Assert.Empty(rig.Bypass.CustomStrategies);
        Assert.Null(rig.Bypass.CustomNotice);
        Assert.Empty(rig.Helper.Saved);
    }

    [Fact]
    public async Task Edit_OpensTheEditorOfThatStrategy()
    {
        await using var rig = new BypassRig();
        rig.Service.StrategyIds.Add("custom:my-isp");
        await rig.OpenAsync();
        rig.Dialogs.WhileEditorOpen = editor =>
        {
            editor.CancelCommand.Execute(null);
            return Task.CompletedTask;
        };

        await rig.Bypass.CustomStrategies.Single().EditCommand.ExecuteAsync(null);

        Assert.Equal(["my-isp"], rig.EditorsOpenedFor);
        Assert.Equal("my-isp", rig.Dialogs.EditorsShown.Single().Name);
        Assert.Equal(0, rig.Service.Count("use"));
    }

    [Fact]
    public async Task Edit_OfTheStrategyInUse_AppliesItAgain_SoTheRunningBypassHasTheNewText()
    {
        await using var rig = new BypassRig();
        rig.Service.StrategyIds.Add("custom:my-isp");
        rig.Service.Strategy = "custom:my-isp";
        await rig.OpenAsync();
        rig.Dialogs.WhileEditorOpen = async editor =>
        {
            editor.Text = "--lua-desync=multisplit:pos=2\n";
            await editor.SaveCommand.ExecuteAsync(null);
        };

        await rig.Bypass.CustomStrategies.Single().EditCommand.ExecuteAsync(null);

        Assert.Equal([("my-isp", "--lua-desync=multisplit:pos=2\n")], rig.Helper.Saved);
        Assert.Equal("custom:my-isp", rig.Service.Sent.Last(r => r.Cmd == "use").Strategy);
        Assert.Equal("custom:my-isp", rig.Bypass.CurrentStrategy);
        Assert.Equal(["custom:my-isp"], rig.Bypass.Strategies.Where(s => s.IsSelected).Select(s => s.Id));
    }

    [Fact]
    public async Task Edit_OfTheStrategyInUse_ThatTheServiceThenRefuses_ShowsItsMessage()
    {
        await using var rig = new BypassRig();
        rig.Service.StrategyIds.Add("custom:my-isp");
        rig.Service.Strategy = "custom:my-isp";
        await rig.OpenAsync();
        rig.Dialogs.WhileEditorOpen = async editor =>
        {
            await editor.SaveCommand.ExecuteAsync(null);
            rig.Service.UseRefusal = "стратегия не прошла проверку: bad option";
        };

        await rig.Bypass.CustomStrategies.Single().EditCommand.ExecuteAsync(null);

        Assert.Equal("стратегия не прошла проверку: bad option", rig.Bypass.ErrorText);
    }

    [Fact]
    public async Task Edit_OfAnotherStrategy_DoesNotTouchTheOneInUse()
    {
        await using var rig = new BypassRig();
        rig.Service.StrategyIds.AddRange(["custom:a", "custom:b"]);
        rig.Service.Strategy = "custom:a";
        await rig.OpenAsync();
        rig.Dialogs.WhileEditorOpen = editor => editor.SaveCommand.ExecuteAsync(null);

        await rig.Bypass.CustomStrategies.Single(c => c.Name == "b").EditCommand.ExecuteAsync(null);

        Assert.Equal(0, rig.Service.Count("use"));
        Assert.Equal("custom:a", rig.Bypass.CurrentStrategy);
    }

    [Fact]
    public async Task Create_WithoutAService_IsNotAvailable()
    {
        await using var rig = new BypassRig();
        rig.Service.Unavailable = true;
        await rig.Monitor.RefreshNowAsync();

        Assert.False(rig.Bypass.CreateCustomCommand.CanExecute(null));
    }

    // ----- deleting -----

    [Fact]
    public async Task Delete_AsksFirst_AndRemovesItThroughTheAdministrator()
    {
        await using var rig = new BypassRig();
        rig.Service.StrategyIds.AddRange(["custom:a", "custom:b"]);
        await rig.OpenAsync();

        await rig.Bypass.CustomStrategies.Single(c => c.Name == "a").DeleteCommand.ExecuteAsync(null);

        Assert.Equal(["Удалить стратегию"], rig.Dialogs.Asked);
        Assert.Equal(["a"], rig.Helper.Deleted);
        Assert.Equal(["b"], rig.Bypass.CustomStrategies.Select(c => c.Name));
        Assert.DoesNotContain(rig.Bypass.Strategies, s => s.Id == "custom:a");
        Assert.Equal("Стратегия «a» удалена.", rig.Bypass.CustomNotice);
    }

    [Fact]
    public async Task Delete_Declined_DoesNothing()
    {
        await using var rig = new BypassRig();
        rig.Service.StrategyIds.Add("custom:a");
        await rig.OpenAsync();
        rig.Dialogs.Answers.Enqueue(false);

        await rig.Bypass.CustomStrategies.Single().DeleteCommand.ExecuteAsync(null);

        Assert.Empty(rig.Helper.Deleted);
        Assert.Single(rig.Bypass.CustomStrategies);
        Assert.Null(rig.Bypass.CustomNotice);
    }

    [Fact]
    public async Task Delete_OfTheStrategyInUse_IsRefusedWithAnExplanation_WithoutEvenAsking()
    {
        await using var rig = new BypassRig();
        rig.Service.StrategyIds.Add("custom:a");
        rig.Service.Strategy = "custom:a";
        await rig.OpenAsync();

        await rig.Bypass.CustomStrategies.Single().DeleteCommand.ExecuteAsync(null);

        Assert.Equal("Стратегия «a» сейчас выбрана: выберите другую, тогда её можно будет удалить.", rig.Bypass.CustomNotice);
        Assert.Empty(rig.Dialogs.Asked);
        Assert.Empty(rig.Helper.Deleted);
    }

    [Fact]
    public async Task Delete_ThatFails_SaysWhy()
    {
        await using var rig = new BypassRig();
        rig.Service.StrategyIds.Add("custom:a");
        await rig.OpenAsync();
        rig.Helper.DeleteResult = new AdminOutcome(false, "Запрос прав администратора отменён.");

        await rig.Bypass.CustomStrategies.Single().DeleteCommand.ExecuteAsync(null);

        Assert.Equal("Запрос прав администратора отменён.", rig.Bypass.CustomNotice);
        Assert.False(rig.Bypass.HasCustomFallback);
        Assert.Single(rig.Bypass.CustomStrategies);   // still there
    }

    [Fact]
    public async Task Delete_WhereNoOneCanBeAskedForAPassword_OffersTheSudoCommand_AndCopiesIt()
    {
        await using var rig = new BypassRig();
        rig.Service.StrategyIds.Add("custom:a");
        await rig.OpenAsync();
        rig.Helper.DeleteResult = new AdminOutcome(false, "Не нашёл pkexec.", "sudo /usr/lib/tgwsone/tgwsone --admin delete-strategy a");

        await rig.Bypass.CustomStrategies.Single().DeleteCommand.ExecuteAsync(null);

        Assert.True(rig.Bypass.HasCustomFallback);
        Assert.Equal("sudo /usr/lib/tgwsone/tgwsone --admin delete-strategy a", rig.Bypass.CustomFallback);

        await rig.Bypass.CopyCustomFallbackCommand.ExecuteAsync(null);
        Assert.Equal(rig.Bypass.CustomFallback, rig.Clipboard.Text);
        Assert.Equal("Команда скопирована", rig.Bypass.CustomNotice);
    }

    [Fact]
    public async Task ANewAction_ClearsTheOldNoticeAndFallback()
    {
        await using var rig = new BypassRig();
        rig.Service.StrategyIds.Add("custom:a");
        await rig.OpenAsync();
        rig.Helper.DeleteResult = new AdminOutcome(false, "нет прав", "sudo x");
        await rig.Bypass.CustomStrategies.Single().DeleteCommand.ExecuteAsync(null);
        Assert.True(rig.Bypass.HasCustomFallback);

        rig.Dialogs.WhileEditorOpen = editor =>
        {
            editor.CancelCommand.Execute(null);
            return Task.CompletedTask;
        };
        await rig.Bypass.CreateCustomCommand.ExecuteAsync(null);

        Assert.False(rig.Bypass.HasCustomFallback);
        Assert.Null(rig.Bypass.CustomNotice);
    }
}
