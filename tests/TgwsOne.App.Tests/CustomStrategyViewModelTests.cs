using TgwsOne.Core.Zapret;
using TgwsOne.Services;
using TgwsOne.ViewModels;

namespace TgwsOne.Tests;

public class CustomStrategyViewModelTests
{
    private const string Valid = "# мой провайдер\n--lua-desync=multisplit:pos=1\n--payload=tls_client_hello\n";

    // ----- the editor itself -----

    [Fact]
    public void ANewStrategy_HasAnEmptyName_AndAnEditableOne()
    {
        using var kit = new EditorKit();

        CustomStrategyViewModel vm = kit.Editor(null, "");

        Assert.True(vm.IsNew);
        Assert.True(vm.NameEditable);
        Assert.Equal("", vm.Name);
        Assert.Equal("Новая своя стратегия", vm.Title);
    }

    [Fact]
    public void AnExistingStrategy_KeepsItsName()
    {
        using var kit = new EditorKit();

        CustomStrategyViewModel vm = kit.Editor("my-isp", Valid);

        Assert.False(vm.IsNew);
        Assert.False(vm.NameEditable);   // another name would be another strategy
        Assert.Equal("my-isp", vm.Name);
        Assert.Equal(Valid, vm.Text);
        Assert.Equal("Своя стратегия «my-isp»", vm.Title);
    }

    [Fact]
    public void TheHelpLink_PointsAtTheManualOfTheBundledRelease()
    {
        Assert.Equal("https://github.com/bol-van/zapret2/blob/v1.0.7/docs/manual.md", CustomStrategyViewModel.HelpUrl);
    }

    [Fact]
    public async Task OpeningTheHelp_OpensTheManual_AndSaysIfItCannot()
    {
        using var kit = new EditorKit();
        CustomStrategyViewModel vm = kit.Editor();

        await vm.OpenHelpCommand.ExecuteAsync(null);
        Assert.Equal([CustomStrategyViewModel.HelpUrl], kit.Launcher.Opened);
        Assert.Null(vm.Notice);

        kit.Launcher.Succeeds = false;
        await vm.OpenHelpCommand.ExecuteAsync(null);
        Assert.StartsWith("Не удалось открыть справку:", vm.Notice);
    }

    [Fact]
    public void Cancel_ClosesWithoutSaving()
    {
        using var kit = new EditorKit();
        CustomStrategyViewModel vm = kit.Editor("x", Valid);
        int closed = 0;
        vm.CloseRequested += () => closed++;

        vm.CancelCommand.Execute(null);

        Assert.Equal(1, closed);
        Assert.False(vm.Saved);
        Assert.Empty(kit.Helper.Saved);
    }

    // ----- checking the name and the text -----

    [Theory]
    [InlineData("")]
    [InlineData("My-Isp")]
    [InlineData("my isp")]
    [InlineData("../x")]
    [InlineData("имя")]
    public async Task ABadName_IsShownUnderTheName_AndNothingIsRunOrSaved(string name)
    {
        using var kit = new EditorKit();
        CustomStrategyViewModel vm = kit.Editor(null, Valid);
        vm.Name = name;

        await vm.CheckCommand.ExecuteAsync(null);
        await vm.SaveCommand.ExecuteAsync(null);

        Assert.Equal("Имя: латиница в нижнем регистре, цифры и дефис, до 32 символов.", vm.NameError);
        Assert.Empty(kit.Runner.Calls);
        Assert.Empty(kit.Helper.Saved);
    }

    [Theory]
    [InlineData("--debug=@/tmp/x\n--lua-desync=multisplit:pos=1", "недопустимый параметр в стратегии: --debug")]
    [InlineData("--qnum=7\n--lua-desync=multisplit:pos=1", "недопустимый параметр в стратегии: --qnum")]
    [InlineData("# ничего\n--payload=tls_client_hello", "в стратегии нет ни одного шага --lua-desync")]
    [InlineData("--lua-desync", "у параметра в стратегии нет значения: --lua-desync")]
    public async Task ABadText_IsShownUnderTheText_AndNothingIsRunOrSaved(string text, string expected)
    {
        using var kit = new EditorKit();
        CustomStrategyViewModel vm = kit.Editor("x", text);

        await vm.CheckCommand.ExecuteAsync(null);
        await vm.SaveCommand.ExecuteAsync(null);

        Assert.Equal(expected, vm.TextError);
        Assert.Null(vm.NameError);
        Assert.Empty(kit.Runner.Calls);
        Assert.Empty(kit.Helper.Saved);
    }

    [Fact]
    public async Task TheErrors_GoWhenTheTextIsFixed()
    {
        using var kit = new EditorKit();
        CustomStrategyViewModel vm = kit.Editor("x", "--qnum=7");
        await vm.SaveCommand.ExecuteAsync(null);
        Assert.NotNull(vm.TextError);

        vm.Text = Valid;
        await vm.SaveCommand.ExecuteAsync(null);

        Assert.Null(vm.TextError);
        Assert.Single(kit.Helper.Saved);
    }

    // ----- the check (a real --dry-run, as the current user) -----

    [Fact]
    public async Task Check_RunsTheBundlesExecutable_WithDryRunAndTheFullCommandLine()
    {
        using var kit = new EditorKit();
        kit.Runner.Output = "command line parameters verified\n";
        CustomStrategyViewModel vm = kit.Editor("my-isp", Valid);

        await vm.CheckCommand.ExecuteAsync(null);

        (string file, string[] args) = Assert.Single(kit.Runner.Calls);
        Assert.Equal(kit.ExePath, file);
        DesyncPart part = CustomStrategies.Parse(Valid).Part!;
        string[] expected = [.. ZapretArgs.Build(kit.Context.Os, kit.Context.Bundle, kit.Context.Lists, "custom:my-isp", kit.Context.StateDir, part)];
        Assert.Equal("--dry-run", args[0]);
        Assert.Equal(expected, args[1..]);
        Assert.Contains("--lua-desync=multisplit:pos=1", args);   // the strategy's own step is in there …
        Assert.Contains("--qnum=3443", args);                      // … and so is everything the service adds around it
    }

    [Fact]
    public async Task Check_Passed_SaysSo_AndShowsWhatTheProgramSaid()
    {
        using var kit = new EditorKit();
        kit.Runner.Output = "command line parameters verified\n";
        CustomStrategyViewModel vm = kit.Editor("x", Valid);

        await vm.CheckCommand.ExecuteAsync(null);

        Assert.True(vm.CheckPassed);
        Assert.Equal("Проверка пройдена.\ncommand line parameters verified", vm.CheckOutput);
    }

    [Fact]
    public async Task Check_Passed_WithNoOutput_JustSaysSo()
    {
        using var kit = new EditorKit();
        CustomStrategyViewModel vm = kit.Editor("x", Valid);

        await vm.CheckCommand.ExecuteAsync(null);

        Assert.True(vm.CheckPassed);
        Assert.Equal("Проверка пройдена.", vm.CheckOutput);
    }

    [Fact]
    public async Task Check_Failed_ShowsTheProgramsOwnWords()
    {
        using var kit = new EditorKit();
        kit.Runner.Code = 1;
        kit.Runner.Output = "bad option --lua-desync=nosuchfunction\n";
        CustomStrategyViewModel vm = kit.Editor("x", Valid);

        await vm.CheckCommand.ExecuteAsync(null);

        Assert.False(vm.CheckPassed);
        Assert.Equal("bad option --lua-desync=nosuchfunction", vm.CheckOutput);
    }

    [Fact]
    public async Task Check_Failed_WithNoOutput_SaysTheExitCode()
    {
        using var kit = new EditorKit();
        kit.Runner.Code = 2;
        CustomStrategyViewModel vm = kit.Editor("x", Valid);

        await vm.CheckCommand.ExecuteAsync(null);

        Assert.Equal("Проверка завершилась с кодом 2.", vm.CheckOutput);
    }

    [Fact]
    public async Task Check_WithoutTheBundle_SaysWhereItShouldBe_AndRunsNothing()
    {
        using var kit = new EditorKit(exeExists: false);
        CustomStrategyViewModel vm = kit.Editor("x", Valid);

        await vm.CheckCommand.ExecuteAsync(null);

        Assert.Equal($"Пакет zapret2 не найден: {kit.ExePath}", vm.CheckOutput);
        Assert.False(vm.CheckPassed);
        Assert.Empty(kit.Runner.Calls);
    }

    [Fact]
    public async Task ANewCheck_ClearsTheOldAnswer()
    {
        using var kit = new EditorKit();
        kit.Runner.Output = "ok";
        CustomStrategyViewModel vm = kit.Editor("x", Valid);
        await vm.CheckCommand.ExecuteAsync(null);
        Assert.NotNull(vm.CheckOutput);

        vm.Text = "--qnum=7";   // now invalid
        await vm.CheckCommand.ExecuteAsync(null);

        Assert.Null(vm.CheckOutput);
        Assert.False(vm.CheckPassed);
    }

    // ----- saving (administrator rights) -----

    [Fact]
    public async Task Save_HandsTheNameAndTheTextToTheAdministrator_AndClosesWhenDone()
    {
        using var kit = new EditorKit();
        CustomStrategyViewModel vm = kit.Editor(null, Valid);
        vm.Name = "my-isp";
        int closed = 0;
        vm.CloseRequested += () => closed++;

        await vm.SaveCommand.ExecuteAsync(null);

        Assert.Equal([("my-isp", Valid)], kit.Helper.Saved);
        Assert.True(vm.Saved);
        Assert.Equal("my-isp", vm.SavedName);
        Assert.Equal(1, closed);
        Assert.Null(vm.Problem);
    }

    [Fact]
    public async Task Save_ThatTheAdministratorSideRefuses_ShowsWhy_AndStaysOpen()
    {
        using var kit = new EditorKit();
        kit.Helper.SaveResult = new AdminOutcome(false, "файл /srv/x.bin недоступен для чтения всем (chmod o+r)");
        CustomStrategyViewModel vm = kit.Editor("x", Valid);
        int closed = 0;
        vm.CloseRequested += () => closed++;

        await vm.SaveCommand.ExecuteAsync(null);

        Assert.False(vm.Saved);
        Assert.Equal(0, closed);
        Assert.Equal("файл /srv/x.bin недоступен для чтения всем (chmod o+r)", vm.Problem);
        Assert.True(vm.ShowProblem);
        Assert.False(vm.HasFallback);
    }

    [Fact]
    public async Task Save_WhereNoOneCanBeAskedForAPassword_OffersTheSudoCommand_AndCopiesIt()
    {
        using var kit = new EditorKit();
        kit.Helper.SaveResult = new AdminOutcome(false, "Не удалось запросить права администратора.", "sudo /usr/lib/tgwsone/tgwsone --admin save-strategy x /tmp/x.args");
        CustomStrategyViewModel vm = kit.Editor("x", Valid);

        await vm.SaveCommand.ExecuteAsync(null);

        Assert.True(vm.HasFallback);
        Assert.False(vm.ShowProblem);   // the panel with the command tells the story
        Assert.Equal("sudo /usr/lib/tgwsone/tgwsone --admin save-strategy x /tmp/x.args", vm.FallbackCommand);

        await vm.CopyFallbackCommand.ExecuteAsync(null);
        Assert.Equal(vm.FallbackCommand, kit.Clipboard.Text);
        Assert.Equal("Команда скопирована", vm.Notice);
    }

    [Fact]
    public async Task ASecondSave_ClearsTheFirstOnesFallback()
    {
        using var kit = new EditorKit();
        kit.Helper.SaveResult = new AdminOutcome(false, "нет прав", "sudo x");
        CustomStrategyViewModel vm = kit.Editor("x", Valid);
        await vm.SaveCommand.ExecuteAsync(null);
        Assert.True(vm.HasFallback);

        kit.Helper.SaveResult = new AdminOutcome(true, "Стратегия сохранена.");
        await vm.SaveCommand.ExecuteAsync(null);

        Assert.False(vm.HasFallback);
        Assert.True(vm.Saved);
    }

    [Fact]
    public async Task WhileSaving_TheButtonsAreOff()
    {
        using var kit = new EditorKit();
        var gate = new TaskCompletionSource<AdminOutcome>();
        var editor = new CustomStrategyViewModel("x", Valid, kit.Context, kit.Runner, new SlowHelper(gate.Task), kit.Clipboard, kit.Launcher);

        Task saving = editor.SaveCommand.ExecuteAsync(null);

        Assert.True(editor.IsBusy);
        Assert.False(editor.SaveCommand.CanExecute(null));
        Assert.False(editor.CheckCommand.CanExecute(null));

        gate.SetResult(new AdminOutcome(true, "ok"));
        await saving;
        Assert.False(editor.IsBusy);
    }

    private sealed class SlowHelper(Task<AdminOutcome> answer) : IElevatedHelper
    {
        public Task<AdminOutcome> SaveStrategyAsync(string name, string text, CancellationToken ct = default) => answer;
        public Task<AdminOutcome> DeleteStrategyAsync(string name, CancellationToken ct = default) => answer;
    }
}
