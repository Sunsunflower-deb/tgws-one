using TgwsOne.Engine.Desync;

namespace TgwsOne.Mobile.Tests;

public class FinderViewModelTests
{
    private static readonly TimeSpan Ms150 = TimeSpan.FromMilliseconds(150);

    private static DesyncStrategy FakeStrategy()
        => new("fake", [new Fake(new FromEnd(0), 8, "www.google.com")]) { Title = "Фейковый ClientHello" };

    private static DesyncStrategy SplitStrategy()
        => new("split-sni", [new Split(new SniEnd(1))]) { Title = "Разрез после имени" };

    private sealed class Rig
    {
        public FakeDesyncControl Control { get; } = new();
        public FinderProbes Probes { get; }
        public FinderViewModel Vm { get; }
        public int CloseRequests { get; private set; }

        public Rig()
        {
            Control.Rotation = [FakeStrategy(), SplitStrategy()];
            Probes = new FinderProbes(Control);
            Vm = new FinderViewModel(new DesyncFinder(Control, Probes, (_, _) => Task.CompletedTask));
            Vm.CloseRequested += () => CloseRequests++;
        }

        public Rig WithExample()
        {
            Probes.Outcomes[DesyncFinder.NoBypassId] = new Outcome(0, false, false, Ms150);
            Probes.Outcomes["fake@2"] = new Outcome(0, false, false, Ms150);
            Probes.Outcomes["fake@4"] = new Outcome(1, false, false, Ms150);
            Probes.Outcomes["fake@8"] = new Outcome(5, true, true, Ms150);
            Probes.Outcomes["fake@12"] = new Outcome(2, false, false, Ms150);
            Probes.Outcomes["split-sni"] = new Outcome(3, false, false, Ms150);
            return this;
        }
    }

    [Fact]
    public void BeforeTheRun_ItExplainsWhatWillHappen()
    {
        var rig = new Rig();

        Assert.Equal(FinderPhase.Confirm, rig.Vm.Phase);
        Assert.True(rig.Vm.IsConfirm);
        Assert.False(rig.Vm.ShowTable);
        Assert.True(rig.Vm.CanCloseNow);
        Assert.Equal("Подбор займёт около минуты и на это время переключит обход.", FinderViewModel.ConfirmText);
        Assert.Empty(rig.Control.Applied);
    }

    [Fact]
    public async Task TheRun_FillsTheTable_AndMarksTheBestCandidate()
    {
        var rig = new Rig().WithExample();

        await rig.Vm.StartCommand.ExecuteAsync(null);

        Assert.Equal(FinderPhase.Done, rig.Vm.Phase);
        Assert.Equal(["Без обхода", "Фейковый ClientHello · TTL 2", "Фейковый ClientHello · TTL 4",
                      "Фейковый ClientHello · TTL 8", "Фейковый ClientHello · TTL 12", "Разрез после имени"],
                     rig.Vm.Rows.Select(row => row.Name));
        Assert.Equal(["0/5", "0/5", "1/5", "5/5", "2/5", "3/5"], rig.Vm.Rows.Select(row => row.TelegramText));
        Assert.Equal([false, false, false, true, false, false], rig.Vm.Rows.Select(row => row.IsBest));
        Assert.Equal("Лучшая стратегия — Фейковый ClientHello · TTL 8.", rig.Vm.Verdict);
        Assert.Equal("Применить «Фейковый ClientHello · TTL 8»", rig.Vm.ApplyText);
        Assert.True(rig.Vm.HasBest);
        Assert.Equal("", rig.Vm.Progress);
    }

    [Fact]
    public async Task WhileItRuns_ItNamesWhatItIsTrying()
    {
        var rig = new Rig().WithExample();
        var seen = new List<string>();
        rig.Vm.PropertyChanged += (_, e) => { if (e.PropertyName == nameof(FinderViewModel.Progress)) seen.Add(rig.Vm.Progress); };

        await rig.Vm.StartCommand.ExecuteAsync(null);

        Assert.Contains("Проверяю: Фейковый ClientHello · TTL 2…", seen);
        Assert.Contains("Проверяю: Разрез после имени…", seen);
        Assert.Contains("Заканчиваю…", seen);
    }

    [Fact]
    public async Task TheMedians_AreShownInMilliseconds()
    {
        var rig = new Rig().WithExample();
        rig.Probes.Outcomes["fake@8"] = new Outcome(5, true, true, TimeSpan.FromMilliseconds(150));

        await rig.Vm.StartCommand.ExecuteAsync(null);

        // 150, 160, 170, 180, 190 ms across the five hosts: the middle one is 170 ms.
        Assert.Equal("170 мс", rig.Vm.Rows.Single(row => row.Name.Contains("TTL 8")).TimeText);
        Assert.Equal("—", rig.Vm.Rows.Single(row => row.Name.Contains("TTL 2")).TimeText);
    }

    [Fact]
    public async Task Apply_ChoosesTheBestCandidate_AndClosesThePage()
    {
        var rig = new Rig().WithExample();
        await rig.Vm.StartCommand.ExecuteAsync(null);

        await rig.Vm.ApplyCommand.ExecuteAsync(null);

        Assert.True(rig.Vm.Applied);
        Assert.Equal("fake@8", rig.Control.Applied[^1]);
        Assert.Equal(1, rig.CloseRequests);
        Assert.True(rig.Vm.CanCloseNow);
    }

    [Fact]
    public async Task Restore_PutsTheBypassBack_AndClosesThePage()
    {
        var rig = new Rig().WithExample();
        await rig.Vm.StartCommand.ExecuteAsync(null);

        await rig.Vm.RestoreCommand.ExecuteAsync(null);

        Assert.True(rig.Control.Restores >= 1);
        Assert.Equal(DesyncFinder.AutoId, rig.Control.Current);
        Assert.Equal(1, rig.CloseRequests);
    }

    [Fact]
    public async Task WhenNothingReachesTelegram_TheVerdictNamesAVpn()
    {
        var rig = new Rig();
        rig.Probes.Outcomes[DesyncFinder.NoBypassId] = new Outcome(0, false, false, Ms150);

        await rig.Vm.StartCommand.ExecuteAsync(null);

        Assert.Equal(DesyncFinder.NoTelegramVerdict, rig.Vm.Verdict);
        Assert.Equal("", rig.Vm.ApplyText);
        Assert.False(rig.Vm.HasBest);
    }

    [Fact]
    public async Task WhenEveryCandidateIsRefused_TheVerdictSaysSo()
    {
        var rig = new Rig();
        rig.Control.Refuse = _ => "не принято";

        await rig.Vm.StartCommand.ExecuteAsync(null);

        Assert.Equal(FinderViewModel.AllRefusedVerdict, rig.Vm.Verdict);
        Assert.All(rig.Vm.Rows, row => Assert.True(row.HasError));
    }

    [Fact]
    public async Task WhenThePlainNetworkAlreadyWorks_ItSaysNoBypassIsNeeded()
    {
        var rig = new Rig();
        rig.Probes.Outcomes[DesyncFinder.NoBypassId] = new Outcome(5, true, true, Ms150);
        rig.Probes.Outcomes["fake@2"] = new Outcome(0, false, false, Ms150);

        await rig.Vm.StartCommand.ExecuteAsync(null);

        Assert.Equal(DesyncFinder.NoBypassNeededVerdict, rig.Vm.Verdict);
        Assert.Contains("split-sni", rig.Control.Applied);   // every candidate was still measured
        Assert.False(rig.Vm.HasBest);
    }

    [Fact]
    public async Task ARefusedCandidate_IsAnErrorRow_AndTheRunGoesOn()
    {
        var rig = new Rig().WithExample();
        rig.Control.Refuse = id => id == "fake@2" ? "стратегия не прошла проверку" : null;

        await rig.Vm.StartCommand.ExecuteAsync(null);

        FinderRowViewModel refused = rig.Vm.Rows.Single(row => row.Name.Contains("TTL 2"));
        Assert.True(refused.HasError);
        Assert.Equal("ошибка: стратегия не прошла проверку", refused.ErrorLine);
        Assert.Equal("ошибка", refused.TelegramText);
        Assert.Equal("Лучшая стратегия — Фейковый ClientHello · TTL 8.", rig.Vm.Verdict);
    }
}
