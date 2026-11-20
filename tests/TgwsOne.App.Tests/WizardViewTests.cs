using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using TgwsOne.ViewModels;
using TgwsOne.Views;

namespace TgwsOne.Tests;

/// <summary>The wizard drawn on the headless platform: each step shows its own controls, and the window ends the wizard however it is closed.</summary>
public class WizardViewTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static async Task<WizardWindow> ShowAsync(WizardRig rig)
    {
        var window = new WizardWindow { DataContext = rig.Vm };
        window.Show();
        await WaitUntilAsync(() => rig.Vm.ConnectFinished);
        Dispatcher.UIThread.RunJobs();
        return window;
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        DateTime limit = DateTime.UtcNow.AddSeconds(5);
        while (!condition())
        {
            if (DateTime.UtcNow > limit) throw new TimeoutException("the condition never became true");
            Dispatcher.UIThread.RunJobs();
            await Task.Delay(10, Ct);
        }
    }

    private static T Find<T>(WizardWindow window, string name) where T : Control
        => window.FindControl<T>(name) ?? throw new InvalidOperationException($"no control named {name}");

    private static void Click(WizardWindow window, string name)
    {
        Button button = Find<Button>(window, name);
        Assert.True(button.IsVisible, $"{name} is not visible");
        Assert.True(button.Command?.CanExecute(null), $"{name} cannot be pressed");
        button.Command!.Execute(null);
        Dispatcher.UIThread.RunJobs();
    }

    [AvaloniaFact]
    public async Task OpeningTheWindow_StartsTheConnection_AndShowsItsResult()
    {
        await using var rig = new WizardRig();
        WizardWindow window = await ShowAsync(rig);

        Assert.Equal(1, rig.ConnectCalls);
        Assert.True(Find<StackPanel>(window, "ConnectPanel").IsVisible);
        Assert.False(Find<StackPanel>(window, "TelegramPanel").IsVisible);
        Assert.False(Find<StackPanel>(window, "DonePanel").IsVisible);
        Assert.Equal("Шаг 1 из 3", Find<TextBlock>(window, "StepTextBlock").Text);
        Assert.Equal("работает · 127.0.0.1:1443", Find<TextBlock>(window, "ProxyLineText").Text);
        Assert.False(Find<StackPanel>(window, "FailurePanel").IsVisible);
        Assert.True(Find<Button>(window, "NextButton").IsVisible);
        Assert.False(Find<Button>(window, "RetryButton").IsVisible);
        Assert.False(Find<Button>(window, "FinishButton").IsVisible);
    }

    [AvaloniaFact]
    public async Task AFailedConnection_ShowsTheProblem_AndRetry()
    {
        await using var rig = new WizardRig { ConnectProblem = "Порт 1443 занят другим приложением." };
        WizardWindow window = await ShowAsync(rig);

        Assert.True(Find<StackPanel>(window, "FailurePanel").IsVisible);
        Assert.Contains("Порт 1443 занят другим приложением.", Find<TextBlock>(window, "ProblemText").Text);
        Assert.Contains("Прокси выключен.", Find<TextBlock>(window, "ProblemText").Text);
        Assert.True(Find<Button>(window, "RetryButton").IsVisible);
        Assert.True(Find<Button>(window, "NextButton").IsEnabled);   // carrying on is allowed

        rig.ConnectProblem = null;
        Click(window, "RetryButton");
        await WaitUntilAsync(() => rig.Vm.Connected);

        Assert.False(Find<StackPanel>(window, "FailurePanel").IsVisible);
        Assert.False(Find<Button>(window, "RetryButton").IsVisible);
        Assert.Equal("работает · 127.0.0.1:1443", Find<TextBlock>(window, "ProxyLineText").Text);
    }

    [AvaloniaFact]
    public async Task TheTelegramStep_ShowsTheHint_Skip_AndOpen_UntilTelegramConnects()
    {
        await using var rig = new WizardRig();
        WizardWindow window = await ShowAsync(rig);

        Click(window, "NextButton");

        Assert.True(Find<StackPanel>(window, "TelegramPanel").IsVisible);
        Assert.False(Find<StackPanel>(window, "ConnectPanel").IsVisible);
        Assert.Equal("Telegram покажет окно «Proxy Server» / «Прокси-сервер» — нажмите «Connect Proxy» / «Подключить прокси»", Find<TextBlock>(window, "TelegramHintText").Text);
        Assert.True(Find<Button>(window, "OpenTelegramButton").IsVisible);
        Assert.True(Find<Button>(window, "SkipButton").IsVisible);
        Assert.False(Find<Button>(window, "NextButton").IsVisible);
        Assert.False(Find<StackPanel>(window, "TelegramDone").IsVisible);

        await rig.TelegramConnectsAsync();
        Dispatcher.UIThread.RunJobs();

        Assert.True(Find<StackPanel>(window, "TelegramDone").IsVisible);
        Assert.False(Find<Button>(window, "SkipButton").IsVisible);
        Assert.True(Find<Button>(window, "NextButton").IsVisible);
    }

    [AvaloniaFact]
    public async Task OpenTelegram_ShowsItsNotice()
    {
        await using var rig = new WizardRig { TelegramNotice = "Telegram не открылся: ссылка скопирована." };
        WizardWindow window = await ShowAsync(rig);
        Click(window, "NextButton");

        Click(window, "OpenTelegramButton");
        await WaitUntilAsync(() => rig.Vm.Notice is not null);

        Assert.True(Find<TextBlock>(window, "WizardNoticeText").IsVisible);
        Assert.Equal("Telegram не открылся: ссылка скопирована.", Find<TextBlock>(window, "WizardNoticeText").Text);
    }

    [AvaloniaFact]
    public async Task TheLastStep_HasTheAutostartBoxChecked_AndFinishClosesTheWindow()
    {
        await using var rig = new WizardRig();
        WizardWindow window = await ShowAsync(rig);
        Click(window, "NextButton");
        Click(window, "SkipButton");

        Assert.True(Find<StackPanel>(window, "DonePanel").IsVisible);
        Assert.Equal("Запускать при входе в систему", Find<CheckBox>(window, "AutostartCheck").Content);
        Assert.True(Find<CheckBox>(window, "AutostartCheck").IsChecked);
        Assert.True(Find<Button>(window, "FinishButton").IsVisible);
        Assert.False(Find<Button>(window, "NextButton").IsVisible);
        Assert.False(Find<Button>(window, "SkipButton").IsVisible);

        Click(window, "FinishButton");

        Assert.False(window.IsVisible);
        Assert.Equal([true], rig.Autostart.Calls);
        Assert.Equal(1, rig.Settings.FirstRunMarks);
    }

    [AvaloniaFact]
    public async Task ClearingTheBox_TurnsAutostartOff()
    {
        await using var rig = new WizardRig();
        WizardWindow window = await ShowAsync(rig);
        Click(window, "NextButton");
        Click(window, "SkipButton");

        Find<CheckBox>(window, "AutostartCheck").IsChecked = false;
        Click(window, "FinishButton");

        Assert.Equal([false], rig.Autostart.Calls);
    }

    [AvaloniaFact]
    public async Task ClosingTheWindowWithTheCross_EndsTheWizardToo_WithoutTouchingAutostart()
    {
        await using var rig = new WizardRig();
        WizardWindow window = await ShowAsync(rig);

        window.Close();

        Assert.Equal(1, rig.Settings.FirstRunMarks);
        Assert.Empty(rig.Autostart.Calls);
    }
}
