using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using TgwsOne.Services;
using TgwsOne.ViewModels;
using TgwsOne.Views;

namespace TgwsOne.Tests;

/// <summary>The finder's window on the headless platform.</summary>
public class FinderDialogTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private sealed class Rig
    {
        public FakeBypassService Service { get; } = new();
        public FakeProbes Probes { get; }
        public FinderViewModel Vm { get; }
        public FinderDialog Dialog { get; }

        public Rig()
        {
            Probes = new FakeProbes(Service);
            Vm = new FinderViewModel(new StrategyFinder(Service, Probes, Probes, (_, _) => Task.CompletedTask));
            Dialog = new FinderDialog { DataContext = Vm };
            Dialog.Show();
            Dispatcher.UIThread.RunJobs();
        }

        public Rig WithTheSpecsExample()
        {
            Probes.Outcomes["fake-tls"] = new Outcome(5, true, TimeSpan.FromMilliseconds(180));
            Probes.Outcomes["split"] = new Outcome(5, true, TimeSpan.FromMilliseconds(150));
            Probes.Outcomes["split-stealth"] = new Outcome(3, false, TimeSpan.FromMilliseconds(150));
            return this;
        }

        public T Find<T>(string name) where T : Control
            => Dialog.FindControl<T>(name) ?? throw new InvalidOperationException($"no control named {name}");

        public string[] Texts(Control root) => [.. root.GetVisualDescendants().OfType<TextBlock>().Where(t => t.IsEffectivelyVisible).Select(t => t.Text ?? "")];

        public async Task RunAsync()
        {
            await Vm.StartCommand.ExecuteAsync(null);
            Dispatcher.UIThread.RunJobs();
        }
    }

    [AvaloniaFact]
    public void Initially_ItAsksBeforeDoingAnything()
    {
        var rig = new Rig();

        Assert.True(rig.Find<StackPanel>("ConfirmPanel").IsVisible);
        Assert.False(rig.Find<StackPanel>("ResultPanel").IsVisible);
        Assert.Equal(FinderViewModel.ConfirmText, rig.Find<TextBlock>("ConfirmText").Text);
        Assert.Equal("Начать подбор", rig.Find<Button>("StartButton").Content);
        Assert.Equal("Подобрать лучшую стратегию", rig.Dialog.Title);
        Assert.Empty(rig.Service.Sent);
    }

    [AvaloniaFact]
    public async Task AfterARun_TheTableShowsEachStrategy_AndTheBestIsTagged()
    {
        var rig = new Rig().WithTheSpecsExample();

        await rig.RunAsync();

        Assert.False(rig.Find<StackPanel>("ConfirmPanel").IsVisible);
        Assert.True(rig.Find<StackPanel>("ResultPanel").IsVisible);
        string[] texts = rig.Texts(rig.Find<ItemsControl>("RowList"));
        Assert.Contains("fake-tls", texts);
        Assert.Contains("split", texts);
        Assert.Contains("split-stealth", texts);
        Assert.Contains("aggressive", texts);
        Assert.Contains("5/5", texts);
        Assert.Contains("3/5", texts);
        Assert.Contains("0/5", texts);
        Assert.Contains("170 мс", texts);
        Assert.Equal(1, texts.Count(t => t == "лучшая"));   // one row carries the tag
        Assert.Equal("Применить «split»", rig.Find<Button>("ApplyButton").Content);
        Assert.True(rig.Find<Button>("ApplyButton").IsVisible);
        Assert.True(rig.Find<Button>("RestoreButton").IsVisible);
        Assert.False(rig.Find<Button>("StopButton").IsVisible);
        Assert.False(rig.Find<StackPanel>("ProgressPanel").IsVisible);
    }

    [AvaloniaFact]
    public async Task TheCloudflareColumn_HasATickOrACrossPerRow()
    {
        var rig = new Rig().WithTheSpecsExample();

        await rig.RunAsync();

        // fake-tls ✓, split ✓, split-stealth ✗, aggressive ✗ — drawn as shapes, in these two tones
        var icons = rig.Find<ItemsControl>("RowList").GetVisualDescendants().OfType<Icon>().Where(i => i.IsEffectivelyVisible).ToList();
        Assert.Equal([Tone.Green, Tone.Green, Tone.Red, Tone.Red], icons.Select(i => i.Tone!.Value));
        Assert.Equal([IconPaths.TickMark, IconPaths.TickMark, IconPaths.CrossMark, IconPaths.CrossMark], icons.Select(i => i.Data));
    }

    [AvaloniaFact]
    public async Task WhenNothingReachesTelegram_TheWindowSaysAVpnIsNeeded_AndOffersNoApply()
    {
        var rig = new Rig();

        await rig.RunAsync();

        Assert.True(rig.Find<TextBlock>("VerdictText").IsVisible);
        Assert.Equal(StrategyFinder.NoTelegramVerdict, rig.Find<TextBlock>("VerdictText").Text);
        Assert.False(rig.Find<Button>("ApplyButton").IsVisible);
        Assert.True(rig.Find<Button>("RestoreButton").IsVisible);
    }

    [AvaloniaFact]
    public async Task WhileTheRunIsOn_ThereIsProgress_AndAStopButton()
    {
        var rig = new Rig().WithTheSpecsExample();
        var gate = new TaskCompletionSource();
        rig.Probes.BeforeProbe = async ct =>
        {
            if (rig.Service.Strategy == "split") await gate.Task.WaitAsync(ct);   // hold split in flight
        };

        Task run = rig.Vm.StartCommand.ExecuteAsync(null);
        while (rig.Probes.Handshakes.Count <= 5) await Task.Delay(10, Ct);
        Dispatcher.UIThread.RunJobs();

        Assert.True(rig.Find<StackPanel>("ProgressPanel").IsVisible);
        Assert.Equal("Проверяю: split…", rig.Find<TextBlock>("ProgressText").Text);
        Assert.True(rig.Find<Button>("StopButton").IsVisible);
        Assert.False(rig.Find<Button>("ApplyButton").IsVisible);
        Assert.Equal(["fake-tls"], rig.Texts(rig.Find<ItemsControl>("RowList")).Where(t => t is "fake-tls" or "split"));   // the finished row is there already

        gate.SetResult();
        await run;
    }

    /// <summary>Lets the window's asynchronous close attempt play out.</summary>
    private static async Task UntilClosed(FinderDialog dialog)
    {
        for (int i = 0; i < 300 && dialog.IsVisible; i++)
        {
            Dispatcher.UIThread.RunJobs();
            await Task.Delay(10, Ct);
        }
    }

    [AvaloniaFact]
    public async Task ClosingTheWindow_AfterARun_PutsTheBypassBack_ThenCloses()
    {
        var rig = new Rig().WithTheSpecsExample();
        await rig.RunAsync();
        Assert.Equal("aggressive", rig.Service.Strategy);   // where the run left it

        rig.Dialog.Close();   // the cross, Esc, Alt+F4: whichever, it arrives as a close request
        await UntilClosed(rig.Dialog);

        Assert.False(rig.Dialog.IsVisible);
        Assert.Equal("auto", rig.Service.Strategy);
    }

    [AvaloniaFact]
    public async Task ClosingTheWindow_DuringTheRun_StopsItAndPutsTheBypassBack()
    {
        var rig = new Rig().WithTheSpecsExample();
        rig.Probes.BeforeProbe = async ct =>
        {
            if (rig.Service.Strategy == "split-stealth") await Task.Delay(Timeout.Infinite, ct);
        };
        Task run = rig.Vm.StartCommand.ExecuteAsync(null);
        while (rig.Service.Strategy != "split-stealth" || rig.Probes.Handshakes.Count <= 10) await Task.Delay(10, Ct);

        rig.Dialog.Close();
        await UntilClosed(rig.Dialog);
        await run;

        Assert.False(rig.Dialog.IsVisible);
        Assert.Equal("auto", rig.Service.Strategy);
        Assert.False(rig.Vm.Applied);
    }

    [AvaloniaFact]
    public async Task ClosingTheWindow_AfterApplying_KeepsTheChosenStrategy()
    {
        var rig = new Rig().WithTheSpecsExample();
        await rig.RunAsync();

        await rig.Vm.ApplyCommand.ExecuteAsync(null);
        await UntilClosed(rig.Dialog);

        Assert.False(rig.Dialog.IsVisible);
        Assert.Equal("split", rig.Service.Strategy);
    }

    [AvaloniaFact]
    public void ClosingTheWindow_BeforeTheRun_JustCloses()
    {
        var rig = new Rig();

        rig.Dialog.Close();
        Dispatcher.UIThread.RunJobs();

        Assert.False(rig.Dialog.IsVisible);
        Assert.Empty(rig.Service.Sent);
    }
}
