using TgwsOne.Core.Zapret;
using TgwsOne.Services;
using TgwsOne.ViewModels;

namespace TgwsOne.Tests;

public class FinderViewModelTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private sealed class Rig
    {
        public FakeBypassService Service { get; } = new();
        public FakeProbes Probes { get; }
        public FinderViewModel Vm { get; }
        public int CloseRequests { get; private set; }

        public Rig()
        {
            Probes = new FakeProbes(Service);
            Vm = new FinderViewModel(new StrategyFinder(Service, Probes, Probes, (_, _) => Task.CompletedTask));
            Vm.CloseRequested += () => CloseRequests++;
        }

        /// <summary>The spec's example: fake-tls 5/5 ✓ 180 ms, split 5/5 ✓ 150 ms (the best), split-stealth 3/5 ✗, aggressive 0/5 ✗.</summary>
        public Rig WithTheSpecsExample()
        {
            Probes.Outcomes["fake-tls"] = new Outcome(5, true, TimeSpan.FromMilliseconds(180));
            Probes.Outcomes["split"] = new Outcome(5, true, TimeSpan.FromMilliseconds(150));
            Probes.Outcomes["split-stealth"] = new Outcome(3, false, TimeSpan.FromMilliseconds(150));
            return this;
        }
    }

    private static async Task WaitUntil(Func<bool> condition)
    {
        DateTime limit = DateTime.UtcNow.AddSeconds(5);
        while (!condition())
        {
            if (DateTime.UtcNow > limit) throw new TimeoutException("the condition never became true");
            await Task.Delay(10, Ct);
        }
    }

    // ----- before the run -----

    [Fact]
    public void Initially_ItAsksFirst_AndNothingHasHappened()
    {
        var rig = new Rig();

        Assert.Equal(FinderPhase.Confirm, rig.Vm.Phase);
        Assert.True(rig.Vm.IsConfirm);
        Assert.False(rig.Vm.ShowTable);
        Assert.True(rig.Vm.CanCloseNow);
        Assert.Equal("Подбор займёт около минуты и на это время переключит обход для всех пользователей этого компьютера.", FinderViewModel.ConfirmText);
        Assert.Empty(rig.Service.Sent);
    }

    [Fact]
    public void CancellingTheQuestion_JustCloses_AndTouchesNothing()
    {
        var rig = new Rig();

        rig.Vm.CancelCommand.Execute(null);

        Assert.Equal(1, rig.CloseRequests);
        Assert.Empty(rig.Service.Sent);
        Assert.False(rig.Vm.Applied);
    }

    // ----- the run -----

    [Fact]
    public async Task TheRun_FillsTheTable_AndMarksTheBestRow()
    {
        var rig = new Rig().WithTheSpecsExample();

        await rig.Vm.StartCommand.ExecuteAsync(null);

        Assert.Equal(FinderPhase.Done, rig.Vm.Phase);
        Assert.Equal(["fake-tls", "split", "split-stealth", "aggressive"], rig.Vm.Rows.Select(r => r.Name));
        Assert.Equal(["5/5", "5/5", "3/5", "0/5"], rig.Vm.Rows.Select(r => r.TelegramText));
        Assert.Equal([true, true, false, false], rig.Vm.Rows.Select(r => r.CloudflareOk));
        Assert.Equal([false, true, false, false], rig.Vm.Rows.Select(r => r.IsBest));
        Assert.Equal("Лучшая стратегия — split.", rig.Vm.Verdict);
        Assert.Equal("Применить «split»", rig.Vm.ApplyText);
        Assert.True(rig.Vm.HasBest);
    }

    [Fact]
    public async Task TheTimes_AreTheMediansInMilliseconds()
    {
        var rig = new Rig().WithTheSpecsExample();

        await rig.Vm.StartCommand.ExecuteAsync(null);

        // fake-tls: 180, 190, 200, 210, 220 → 200 ms; split: 150 … 190 → 170 ms; split-stealth: 150, 160, 170 → 160 ms
        Assert.Equal(["200 мс", "170 мс", "160 мс", "—"], rig.Vm.Rows.Select(r => r.TimeText));
    }

    [Fact]
    public async Task WhileItRuns_ItSaysWhichStrategyIsBeingTried()
    {
        var rig = new Rig().WithTheSpecsExample();
        var progress = new List<string>();
        rig.Vm.PropertyChanged += (_, e) => { if (e.PropertyName == nameof(FinderViewModel.Progress)) progress.Add(rig.Vm.Progress); };

        await rig.Vm.StartCommand.ExecuteAsync(null);

        Assert.Contains("Проверяю: fake-tls…", progress);
        Assert.Contains("Проверяю: split…", progress);
        Assert.Contains("Проверяю: aggressive…", progress);
        Assert.Contains("Заканчиваю…", progress);
        Assert.Equal("", rig.Vm.Progress);   // nothing left to report once it is done
    }

    [Fact]
    public async Task WhenNothingReachesTelegram_TheVerdictSaysAVpnIsNeeded()
    {
        var rig = new Rig();   // no outcomes: every strategy lets nothing through

        await rig.Vm.StartCommand.ExecuteAsync(null);

        Assert.Equal(FinderPhase.Done, rig.Vm.Phase);
        Assert.Equal(StrategyFinder.NoTelegramVerdict, rig.Vm.Verdict);
        Assert.False(rig.Vm.HasBest);
        Assert.Equal("", rig.Vm.ApplyText);
        Assert.All(rig.Vm.Rows, r => Assert.False(r.IsBest));
        Assert.False(rig.Vm.ApplyCommand.CanExecute(null));   // nothing to apply
        Assert.True(rig.Vm.RestoreCommand.CanExecute(null));  // but what was there can still be put back
    }

    [Fact]
    public async Task WhenTheServiceRefusesEveryStrategy_TheVerdictPointsAtTheLog()
    {
        var rig = new Rig();
        rig.Service.UseRefusal = "стратегия не прошла проверку";

        await rig.Vm.StartCommand.ExecuteAsync(null);

        Assert.All(rig.Vm.Rows, r => Assert.True(r.HasError));
        Assert.Equal("ошибка: стратегия не прошла проверку", rig.Vm.Rows[0].ErrorLine);
        Assert.Equal(FinderViewModel.AllRefusedVerdict, rig.Vm.Verdict);
    }

    [Fact]
    public async Task WithNoService_ItSaysSo_AndDoesNotCrash()
    {
        var rig = new Rig();
        rig.Service.Unavailable = true;

        await rig.Vm.StartCommand.ExecuteAsync(null);

        Assert.Equal(FinderPhase.Done, rig.Vm.Phase);
        Assert.Equal("служба обхода DPI недоступна", rig.Vm.ErrorText);
        Assert.Empty(rig.Vm.Rows);
    }

    // ----- applying -----

    [Fact]
    public async Task Apply_ChoosesTheBestStrategy_AndCloses_WithoutRestoring()
    {
        var rig = new Rig().WithTheSpecsExample();
        await rig.Vm.StartCommand.ExecuteAsync(null);
        Assert.Equal("aggressive", rig.Service.Strategy);   // the last one tried is where the run ends

        await rig.Vm.ApplyCommand.ExecuteAsync(null);

        Assert.Equal("split", rig.Service.Strategy);
        Assert.Equal("split", rig.Service.Sent.Last(r => r.Cmd == "use").Strategy);
        Assert.True(rig.Vm.Applied);
        Assert.Equal(1, rig.CloseRequests);
        Assert.True(rig.Vm.CanCloseNow);
    }

    [Fact]
    public async Task Apply_ThatIsRefused_ShowsWhy_KeepsTheDialogOpen_AndRestoreStillWorks()
    {
        var rig = new Rig().WithTheSpecsExample();
        await rig.Vm.StartCommand.ExecuteAsync(null);
        rig.Service.UseRefusal = "проверка параметров не прошла";

        await rig.Vm.ApplyCommand.ExecuteAsync(null);

        Assert.Equal("проверка параметров не прошла", rig.Vm.ErrorText);
        Assert.False(rig.Vm.Applied);
        Assert.Equal(0, rig.CloseRequests);

        rig.Service.UseRefusal = null;
        await rig.Vm.RestoreCommand.ExecuteAsync(null);

        Assert.Equal("auto", rig.Service.Strategy);
        Assert.Equal(1, rig.CloseRequests);   // the old message did not hold the dialog open
    }

    [Fact]
    public async Task Restore_PutsTheBypassBackAsItWas_AndCloses()
    {
        var rig = new Rig().WithTheSpecsExample();
        rig.Service.Strategy = "aggressive";
        await rig.Vm.StartCommand.ExecuteAsync(null);

        await rig.Vm.RestoreCommand.ExecuteAsync(null);

        Assert.Equal("aggressive", rig.Service.Strategy);
        Assert.False(rig.Vm.Applied);
        Assert.Equal(1, rig.CloseRequests);
    }

    [Fact]
    public async Task ABypassThatWasOff_IsOffAgain_AfterRestore()
    {
        var rig = new Rig().WithTheSpecsExample();
        rig.Service.Enabled = false;
        await rig.Vm.StartCommand.ExecuteAsync(null);
        Assert.True(rig.Service.Enabled);   // it had to be on for the measurements

        await rig.Vm.RestoreCommand.ExecuteAsync(null);

        Assert.False(rig.Service.Enabled);
        Assert.Equal("auto", rig.Service.Strategy);
    }

    [Fact]
    public async Task ARestoreThatFails_ShowsTheMessage_AndLetsTheWindowClose()
    {
        var rig = new Rig().WithTheSpecsExample();
        await rig.Vm.StartCommand.ExecuteAsync(null);
        rig.Service.Unavailable = true;

        await rig.Vm.RestoreCommand.ExecuteAsync(null);

        Assert.Equal("служба обхода DPI недоступна", rig.Vm.ErrorText);
        Assert.Equal(0, rig.CloseRequests);   // the message stays on screen …
        Assert.True(rig.Vm.CanCloseNow);      // … and the cross still works
    }

    // ----- leaving by other doors -----

    [Fact]
    public async Task Cancel_DuringTheRun_StopsIt_PutsTheBypassBack_AndCloses()
    {
        var rig = new Rig().WithTheSpecsExample();
        rig.Probes.BeforeProbe = async ct =>
        {
            if (rig.Service.Strategy == "split") await Task.Delay(Timeout.Infinite, ct);   // hang while split is being tried
        };

        Task run = rig.Vm.StartCommand.ExecuteAsync(null);
        await WaitUntil(() => rig.Probes.Handshakes.Count > 5);   // fake-tls is done, split is in flight
        Assert.Equal("split", rig.Service.Strategy);
        Assert.False(rig.Vm.CanCloseNow);

        rig.Vm.CancelCommand.Execute(null);
        await run;

        Assert.Equal("auto", rig.Service.Strategy);
        Assert.Equal(1, rig.CloseRequests);
        Assert.False(rig.Vm.Applied);
        Assert.True(rig.Vm.CanCloseNow);
    }

    [Fact]
    public async Task ClosingTheWindow_DuringTheRun_DoesTheSame()
    {
        var rig = new Rig().WithTheSpecsExample();
        rig.Probes.BeforeProbe = async ct =>
        {
            if (rig.Service.Strategy == "split-stealth") await Task.Delay(Timeout.Infinite, ct);
        };
        Task run = rig.Vm.StartCommand.ExecuteAsync(null);
        await WaitUntil(() => rig.Service.Strategy == "split-stealth" && rig.Probes.Handshakes.Count > 10);

        await rig.Vm.ResolveAsync();   // what the window does on its first close attempt
        await run;

        Assert.Equal("auto", rig.Service.Strategy);
        Assert.True(rig.Vm.CanCloseNow);
    }

    [Fact]
    public async Task ClosingTheWindow_AfterTheRun_WithoutChoosing_PutsTheBypassBack()
    {
        var rig = new Rig().WithTheSpecsExample();
        await rig.Vm.StartCommand.ExecuteAsync(null);
        Assert.False(rig.Vm.CanCloseNow);   // something is still to be put back

        await rig.Vm.ResolveAsync();

        Assert.Equal("auto", rig.Service.Strategy);
        Assert.True(rig.Vm.CanCloseNow);
    }

    [Fact]
    public async Task ClosingTheWindow_AfterApplying_LeavesTheChoiceAlone()
    {
        var rig = new Rig().WithTheSpecsExample();
        await rig.Vm.StartCommand.ExecuteAsync(null);
        await rig.Vm.ApplyCommand.ExecuteAsync(null);
        int uses = rig.Service.Count("use");

        await rig.Vm.ResolveAsync();

        Assert.Equal("split", rig.Service.Strategy);
        Assert.Equal(uses, rig.Service.Count("use"));
    }

    [Fact]
    public async Task TheButtons_AreOnlyAvailable_WhenTheyMeanSomething()
    {
        var rig = new Rig().WithTheSpecsExample();
        Assert.False(rig.Vm.ApplyCommand.CanExecute(null));    // nothing to apply yet
        Assert.False(rig.Vm.RestoreCommand.CanExecute(null));  // nothing to restore yet

        await rig.Vm.StartCommand.ExecuteAsync(null);
        Assert.True(rig.Vm.ApplyCommand.CanExecute(null));
        Assert.True(rig.Vm.RestoreCommand.CanExecute(null));

        await rig.Vm.RestoreCommand.ExecuteAsync(null);
        Assert.False(rig.Vm.ApplyCommand.CanExecute(null));    // it is settled
    }

    [Fact]
    public async Task BypassPage_FindBest_OpensTheFinder_AndCatchesUpWhenItCloses()
    {
        await using var page = new BypassRig();
        await page.OpenAsync();
        page.Probes.Outcomes["fake-tls"] = new Outcome(5, true, TimeSpan.FromMilliseconds(100));
        page.Dialogs.WhileFinderOpen = async finder =>
        {
            await finder.StartCommand.ExecuteAsync(null);
            await finder.ApplyCommand.ExecuteAsync(null);   // the user chose the best
        };

        await page.Bypass.FindBestCommand.ExecuteAsync(null);

        Assert.Equal(1, page.Dialogs.FindersShown);
        Assert.Equal("fake-tls", page.Bypass.CurrentStrategy);   // the page shows the choice, not what the run last tried
        Assert.Equal(["fake-tls"], page.Bypass.Strategies.Where(s => s.IsSelected).Select(s => s.Id));
        Assert.True(page.Bypass.CanChange);
    }

    [Fact]
    public async Task BypassPage_AfterACancelledFinder_StillShowsTheOriginalStrategy()
    {
        await using var page = new BypassRig();
        await page.OpenAsync();
        page.Dialogs.WhileFinderOpen = async finder =>
        {
            await finder.StartCommand.ExecuteAsync(null);
            await finder.RestoreCommand.ExecuteAsync(null);
        };

        await page.Bypass.FindBestCommand.ExecuteAsync(null);

        Assert.Equal("auto", page.Bypass.CurrentStrategy);
        Assert.Equal(["auto"], page.Bypass.Strategies.Where(s => s.IsSelected).Select(s => s.Id));
    }
}
