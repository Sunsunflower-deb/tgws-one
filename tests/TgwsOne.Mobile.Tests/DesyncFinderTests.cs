using TgwsOne.Engine.Desync;

namespace TgwsOne.Mobile.Tests;

public class DesyncFinderTests
{
    private static CancellationToken Ct => CancellationToken.None;

    private static readonly TimeSpan Ms150 = TimeSpan.FromMilliseconds(150);

    private static DesyncStrategy FakeStrategy(string id = "fake")
        => new(id, [new Fake(new FromEnd(0), 8, "www.google.com")]);

    private static DesyncStrategy SplitStrategy(string id = "split-sni")
        => new(id, [new Split(new SniEnd(1))]);

    private static DesyncStrategy FakeSplitStrategy()
        => new("fake-split", [new Fake(new SniEnd(1), null, null), new Split(new SniEnd(1))]);

    private sealed class Rig
    {
        public FakeDesyncControl Control { get; } = new();
        public FinderProbes Probes { get; }
        public DesyncFinder Finder { get; }
        public List<TimeSpan> Delays { get; } = [];

        public Rig(params DesyncStrategy[] rotation)
        {
            Control.Rotation = rotation;
            Probes = new FinderProbes(Control);
            Finder = new DesyncFinder(Control, Probes, (delay, _) =>
            {
                lock (Delays) Delays.Add(delay);
                return Task.CompletedTask;
            });
        }
    }

    private static async Task<List<FinderRow>> Collect(IAsyncEnumerable<FinderRow> rows)
    {
        var all = new List<FinderRow>();
        await foreach (FinderRow row in rows) all.Add(row);
        return all;
    }

    private static FinderRow Row(string id, int telegram, bool cloudflare, bool self, int? ms = null, string? error = null)
        => new(id, telegram, cloudflare, self, ms is { } value ? TimeSpan.FromMilliseconds(value) : null, error);

    // ----- candidates -----

    [Fact]
    public void Expand_KeepsTheRotationOrder_AndMultipliesOnlyFakeStrategies()
    {
        DesyncStrategy[] rotation = [FakeStrategy(), SplitStrategy(), FakeSplitStrategy()];

        IReadOnlyList<string> candidates = DesyncFinder.Expand(rotation);

        Assert.Equal([
            "fake@2", "fake@4", "fake@8", "fake@12",
            "split-sni",
            "fake-split@2", "fake-split@4", "fake-split@8", "fake-split@12",
        ], candidates);
    }

    [Fact]
    public void Expand_ADisorderStrategyWithoutFake_IsOneCandidate()
    {
        var rotation = new[] { new DesyncStrategy("disorder-1", [new Disorder(new Absolute(1))]) };

        Assert.Equal(["disorder-1"], DesyncFinder.Expand(rotation));
    }

    [Fact]
    public void TryParseCandidate_SplitsTheFakeTtl_AndLeavesPlainIdsAlone()
    {
        Assert.True(DesyncFinder.TryParseCandidate("fake@4", out string fake, out int? ttl));
        Assert.Equal(("fake", (int?)4), (fake, ttl));

        Assert.True(DesyncFinder.TryParseCandidate("split-sni", out string split, out int? none));
        Assert.Equal(("split-sni", (int?)null), (split, none));
    }

    [Fact]
    public void WithFakeTtl_ReplacesEveryFakeStep_AndLeavesTheRestAlone()
    {
        DesyncStrategy withTtl = DesyncFinder.WithFakeTtl(FakeSplitStrategy(), 2);

        Assert.Single(withTtl.Steps.OfType<Fake>());
        Assert.All(withTtl.Steps.OfType<Fake>(), step => Assert.Equal(2, step.Ttl));
        Assert.Single(withTtl.Steps.OfType<Split>());
    }

    // ----- scoring -----

    [Fact]
    public void Best_MostTelegramHandshakesWins_BeforeAnythingElse()
    {
        var rows = new[] { Row("a", 3, true, true, 50), Row("b", 5, false, false, 900), Row("c", 4, true, true, 60) };

        Assert.Equal("b", DesyncFinder.Best(rows)!.StrategyId);
    }

    [Fact]
    public void Best_ThenTheEndToEndCheck()
    {
        var rows = new[] { Row("a", 5, true, false, 100), Row("b", 5, false, true, 500), Row("c", 5, true, false, 90) };

        Assert.Equal("b", DesyncFinder.Best(rows)!.StrategyId);
    }

    [Fact]
    public void Best_ThenTheLowestMedianTime()
    {
        var rows = new[] { Row("a", 5, true, true, 200), Row("b", 5, true, true, 150), Row("c", 5, true, true, 180) };

        Assert.Equal("b", DesyncFinder.Best(rows)!.StrategyId);
    }

    [Fact]
    public void Best_EqualRows_KeepTheOrderTheyWereTriedIn()
    {
        var rows = new[] { Row("first", 5, true, true, 150), Row("second", 5, true, true, 150) };

        Assert.Equal("first", DesyncFinder.Best(rows)!.StrategyId);
    }

    [Fact]
    public void Best_ARowWithoutATimeLosesATie()
    {
        var rows = new[] { Row("none", 2, false, false), Row("timed", 2, false, false, 800) };

        Assert.Equal("timed", DesyncFinder.Best(rows)!.StrategyId);
    }

    [Fact]
    public void Best_WhenNoneReachedTelegram_IsNull()
    {
        var rows = new[] { Row("a", 0, true, true, 50), Row("b", 0, false, false), Row("c", 0, false, false, error: "не прошла") };

        Assert.Null(DesyncFinder.Best(rows));
        Assert.Null(DesyncFinder.Best([]));
    }

    [Fact]
    public void TheVerdictForNoTelegram_IsTakenVerbatimFromTheDesktopFinder()
    {
        Assert.Equal(
            "Ни одна стратегия не дала связи с серверами Telegram: похоже, провайдер блокирует Telegram по IP-адресу. Здесь поможет только VPN.",
            DesyncFinder.NoTelegramVerdict);
    }

    [Fact]
    public void NoBypassNeeded_OnlyWhenTheBaselineGotEverythingThrough()
    {
        Assert.True(DesyncFinder.NoBypassNeeded(Row("none", 5, true, true, 100)));
        Assert.False(DesyncFinder.NoBypassNeeded(Row("none", 5, true, false, 100)));
        Assert.False(DesyncFinder.NoBypassNeeded(Row("none", 4, true, true, 100)));
    }

    // ----- the run -----

    [Fact]
    public async Task Run_MeasuresTheBaselineWithoutBypassFirst_ThenEveryCandidateInOrder()
    {
        var rig = new Rig(FakeStrategy(), SplitStrategy());
        rig.Control.Baseline = new FinderBaseline(DesyncFinder.AutoId, true);
        rig.Probes.Outcomes[DesyncFinder.NoBypassId] = new Outcome(0, false, false, Ms150);
        rig.Probes.Outcomes["fake@2"] = new Outcome(5, true, true, Ms150);
        rig.Probes.Outcomes["split-sni"] = new Outcome(3, false, false, Ms150);
        FinderBaseline baseline = await rig.Finder.CaptureAsync(Ct);

        List<FinderRow> rows = await Collect(rig.Finder.RunAsync(rig.Finder.Candidates(), baseline, Ct));

        Assert.Equal([DesyncFinder.NoBypassId, "fake@2", "fake@4", "fake@8", "fake@12", "split-sni"],
                     rows.Select(row => row.StrategyId));
        Assert.Equal([DesyncFinder.NoBypassId, "fake@2", "fake@4", "fake@8", "fake@12", "split-sni"],
                     rig.Control.Applied);
        Assert.Equal(1, rig.Control.Restores);
    }

    [Fact]
    public async Task Run_MeasuresEachCandidateWhileItIsTheOneSelected()
    {
        var rig = new Rig(FakeStrategy(), SplitStrategy());
        rig.Probes.Outcomes["fake@8"] = new Outcome(1, false, false, Ms150);
        rig.Probes.Outcomes["split-sni"] = new Outcome(4, true, true, Ms150);
        FinderBaseline baseline = await rig.Finder.CaptureAsync(Ct);

        List<FinderRow> rows = await Collect(rig.Finder.RunAsync(["fake@8", "split-sni"], baseline, Ct));

        Assert.Equal([1, 4], rows.Skip(1).Select(row => row.TelegramOk));
    }

    [Fact]
    public async Task Run_WaitsForTheEngineToSettle_BeforeEveryRow()
    {
        var rig = new Rig(FakeStrategy(), SplitStrategy());
        FinderBaseline baseline = await rig.Finder.CaptureAsync(Ct);

        await Collect(rig.Finder.RunAsync(["fake@8", "split-sni"], baseline, Ct));

        Assert.Equal([TimeSpan.FromMilliseconds(1500), TimeSpan.FromMilliseconds(1500), TimeSpan.FromMilliseconds(1500)],
                     rig.Delays);
    }

    [Fact]
    public async Task Run_ProbesFiveTelegramHosts_TheCfProxyDomain_AndTheEngineExchange()
    {
        var rig = new Rig(SplitStrategy());
        FinderBaseline baseline = await rig.Finder.CaptureAsync(Ct);

        await Collect(rig.Finder.RunAsync(["split-sni"], baseline, Ct));

        var telegram = rig.Probes.Calls.Where(call => call.Kind == "bypass" && call.Route == "ws").ToList();
        Assert.Equal(["kws1.web.telegram.org", "kws2.web.telegram.org", "kws3.web.telegram.org",
                      "kws4.web.telegram.org", "kws5.web.telegram.org"],
                     telegram.Select(call => call.Host).Distinct().Order());
        Assert.Equal(2 * DesyncFinder.TelegramHosts.Count, telegram.Count);
        Assert.All(telegram, call => Assert.Equal((443, TimeSpan.FromSeconds(5)), (call.Port, call.Timeout)));

        var cloudflare = rig.Probes.Calls.Where(call => call.Route == "cf-proxy").ToList();
        Assert.Equal(2, cloudflare.Count);   // baseline and the candidate
        Assert.All(cloudflare, call => Assert.Equal("kws1.cf.example", call.Host));

        var selfChecks = rig.Probes.Calls.Where(call => call.Kind == "self").ToList();
        Assert.Equal(2, selfChecks.Count);
        Assert.All(selfChecks, call => Assert.Equal(TimeSpan.FromSeconds(5), call.Timeout));
    }

    [Fact]
    public async Task Run_AMedianOfTheHandshakeTimes_IsAMeasuredValue()
    {
        var rig = new Rig(FakeStrategy(), SplitStrategy());
        rig.Probes.Outcomes["fake@8"] = new Outcome(5, true, true, Ms150);   // 150, 160, 170, 180, 190 ms
        rig.Probes.Outcomes["split-sni"] = new Outcome(4, true, true, Ms150);   // 150, 160, 170, 180: the lower middle
        FinderBaseline baseline = await rig.Finder.CaptureAsync(Ct);

        List<FinderRow> rows = await Collect(rig.Finder.RunAsync(["fake@8", "split-sni"], baseline, Ct));

        Assert.Equal(TimeSpan.FromMilliseconds(170), rows[1].Median);
        Assert.Equal(TimeSpan.FromMilliseconds(160), rows[2].Median);
    }

    [Fact]
    public async Task Run_ARefusedCandidate_IsAnErrorRow_AndTheNextOneIsStillTried()
    {
        var rig = new Rig(FakeStrategy(), SplitStrategy());
        rig.Control.Refuse = id => id == "fake@2" ? "стратегия не прошла проверку" : null;
        rig.Probes.Outcomes["split-sni"] = new Outcome(5, true, true, Ms150);
        FinderBaseline baseline = await rig.Finder.CaptureAsync(Ct);

        List<FinderRow> rows = await Collect(rig.Finder.RunAsync(["fake@2", "split-sni"], baseline, Ct));

        Assert.Equal("стратегия не прошла проверку", rows[1].Error);
        Assert.Equal(0, rows[1].TelegramOk);
        Assert.Null(rows[2].Error);
        Assert.Equal(5, rows[2].TelegramOk);
        Assert.Equal(2 * 5 + 2 + 2, rig.Probes.Calls.Count(call => call.Kind is "bypass" or "self"));   // the refused candidate was never probed
    }

    [Fact]
    public async Task Run_ABaselineThatCannotBeSelected_IsAnErrorRow_AndCandidatesAreStillTried()
    {
        var rig = new Rig(SplitStrategy());
        rig.Control.Refuse = id => id == DesyncFinder.NoBypassId ? "движок не принял" : null;
        rig.Probes.Outcomes["split-sni"] = new Outcome(5, true, true, Ms150);
        FinderBaseline baseline = await rig.Finder.CaptureAsync(Ct);

        List<FinderRow> rows = await Collect(rig.Finder.RunAsync(["split-sni"], baseline, Ct));

        Assert.Equal("движок не принял", rows[0].Error);
        Assert.Equal("split-sni", rows[1].StrategyId);
        Assert.Equal(5, rows[1].TelegramOk);
    }

    [Fact]
    public async Task Run_Cancelled_StopsAndPutsTheBaselineBack()
    {
        var rig = new Rig(FakeStrategy(), SplitStrategy());
        rig.Control.Baseline = new FinderBaseline("split-sni", false);
        FinderBaseline baseline = await rig.Finder.CaptureAsync(Ct);
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        int calls = 0;
        rig.Probes.BeforeProbe = async ct =>
        {
            // The baseline's seven probes pass; the first probe of the next candidate cancels the run.
            if (Interlocked.Increment(ref calls) > 7)
            {
                await cts.CancelAsync();
                ct.ThrowIfCancellationRequested();
            }
        };

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            Collect(rig.Finder.RunAsync(["fake@2", "split-sni"], baseline, cts.Token)));

        Assert.Equal([DesyncFinder.NoBypassId, "fake@2"], rig.Control.Applied);   // no further candidate was started
        Assert.Equal(1, rig.Control.Restores);
        Assert.Equal("split-sni", rig.Control.Current);
    }

    // ----- baseline, restore, apply -----

    [Fact]
    public async Task Capture_ReadsTheStrategyAndWhetherTheBypassIsOn()
    {
        var rig = new Rig(SplitStrategy());
        rig.Control.Baseline = new FinderBaseline("split-sni", false);

        FinderBaseline baseline = await rig.Finder.CaptureAsync(Ct);

        Assert.Equal(new FinderBaseline("split-sni", false), baseline);
    }

    [Fact]
    public async Task Restore_AndApply_AreHandedToTheControl()
    {
        var rig = new Rig(SplitStrategy());
        FinderBaseline baseline = new("split-sni", true);

        Assert.Null(await rig.Finder.RestoreAsync(baseline, Ct));
        Assert.Null(await rig.Finder.ApplyAsync("split-sni", Ct));

        Assert.Equal(1, rig.Control.Restores);
        Assert.Equal(["split-sni"], rig.Control.Applied);
    }
}
