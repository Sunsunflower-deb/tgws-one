using TgwsOne.Core.Zapret;
using TgwsOne.Services;

namespace TgwsOne.Tests;

public class StrategyFinderTests
{
    private static readonly TimeSpan Ms150 = TimeSpan.FromMilliseconds(150);

    private sealed class Rig
    {
        public FakeBypassService Service { get; } = new();
        public FakeProbes Probes { get; }
        public StrategyFinder Finder { get; }
        public List<TimeSpan> Delays { get; } = [];

        public Rig()
        {
            Probes = new FakeProbes(Service);
            Finder = new StrategyFinder(Service, Probes, Probes, (d, _) =>
            {
                lock (Delays) Delays.Add(d);
                return Task.CompletedTask;
            });
        }
    }

    private static async Task<List<FinderRow>> Collect(IAsyncEnumerable<FinderRow> rows)
    {
        var all = new List<FinderRow>();
        await foreach (FinderRow r in rows) all.Add(r);
        return all;
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static readonly string[] Rotation = ["fake-tls", "split", "split-stealth", "aggressive"];

    // ----- scoring -----

    private static FinderRow Row(string id, int telegram, bool cloudflare, int? ms = null, string? error = null)
        => new(id, telegram, cloudflare, ms is { } m ? TimeSpan.FromMilliseconds(m) : null, error);

    [Fact]
    public void Best_MostTelegramHandshakesWins_BeforeAnythingElse()
    {
        var rows = new[] { Row("a", 3, true, 50), Row("b", 5, false, 900), Row("c", 4, true, 60) };

        Assert.Equal("b", StrategyFinder.Best(rows)!.StrategyId);
    }

    [Fact]
    public void Best_ThenACompleteCloudflareDownload()
    {
        var rows = new[] { Row("a", 5, false, 100), Row("b", 5, true, 500), Row("c", 5, false, 90) };

        Assert.Equal("b", StrategyFinder.Best(rows)!.StrategyId);
    }

    [Fact]
    public void Best_ThenTheLowestMedianTime()
    {
        var rows = new[] { Row("a", 5, true, 200), Row("b", 5, true, 150), Row("c", 5, true, 180) };

        Assert.Equal("b", StrategyFinder.Best(rows)!.StrategyId);
    }

    [Fact]
    public void Best_EqualRows_KeepTheOrderTheyWereTriedIn()
    {
        var rows = new[] { Row("first", 5, true, 150), Row("second", 5, true, 150) };

        Assert.Equal("first", StrategyFinder.Best(rows)!.StrategyId);
    }

    [Fact]
    public void Best_ARowWithoutATimeLosesATie()
    {
        var rows = new[] { Row("none", 2, false), Row("timed", 2, false, 800) };

        Assert.Equal("timed", StrategyFinder.Best(rows)!.StrategyId);
    }

    [Fact]
    public void Best_WhenNoneReachedTelegram_IsNull()
    {
        var rows = new[] { Row("a", 0, true, 50), Row("b", 0, false), Row("c", 0, false, error: "не прошла проверку") };

        Assert.Null(StrategyFinder.Best(rows));
        Assert.Null(StrategyFinder.Best([]));
    }

    [Fact]
    public void TheVerdictForNoTelegram_SaysTheProviderBlocksItByIp_AndAVpnIsNeeded()
    {
        Assert.Contains("по IP-адресу", StrategyFinder.NoTelegramVerdict);
        Assert.Contains("VPN", StrategyFinder.NoTelegramVerdict);
    }

    // ----- candidates -----

    [Fact]
    public async Task Candidates_AreTheFourRotationStrategies_ThenTheCustomOnes_AndNotAuto()
    {
        var rig = new Rig();
        rig.Service.StrategyIds.AddRange(["custom:my-isp", "custom:other"]);

        IReadOnlyList<string> candidates = await rig.Finder.CandidatesAsync(Ct);

        Assert.Equal([.. Rotation, "custom:my-isp", "custom:other"], candidates);
    }

    // ----- the run -----

    [Fact]
    public async Task Run_TriesEveryCandidateInOrder_AndMeasuresEach()
    {
        var rig = new Rig();
        rig.Probes.Outcomes["fake-tls"] = new Outcome(5, true, Ms150);
        rig.Probes.Outcomes["split"] = new Outcome(3, false, Ms150);
        FinderBaseline baseline = await rig.Finder.CaptureAsync(Ct);

        List<FinderRow> rows = await Collect(rig.Finder.RunAsync(Rotation, baseline, Ct));

        Assert.Equal(Rotation, rows.Select(r => r.StrategyId));
        Assert.Equal(["fake-tls", "split", "split-stealth", "aggressive"], rig.Service.Sent.Where(r => r.Cmd == "use").Select(r => r.Strategy));
        Assert.Equal((5, true), (rows[0].TelegramOk, rows[0].CloudflareOk));
        Assert.Equal((3, false), (rows[1].TelegramOk, rows[1].CloudflareOk));
        Assert.Equal((0, false), (rows[2].TelegramOk, rows[2].CloudflareOk));   // no entry: nothing gets through
        Assert.All(rows, r => Assert.Null(r.Error));
    }

    [Fact]
    public async Task Run_MeasuresEachStrategyWhileItIsTheOneRunning()
    {
        // The probes read the service's strategy at the moment they run: a finder that measured before `use` took effect would
        // credit one strategy with another's result.
        var rig = new Rig();
        rig.Probes.Outcomes["fake-tls"] = new Outcome(1, false, Ms150);
        rig.Probes.Outcomes["split"] = new Outcome(4, true, Ms150);
        FinderBaseline baseline = await rig.Finder.CaptureAsync(Ct);

        List<FinderRow> rows = await Collect(rig.Finder.RunAsync(["fake-tls", "split"], baseline, Ct));

        Assert.Equal([1, 4], rows.Select(r => r.TelegramOk));
    }

    [Fact]
    public async Task Run_WaitsForTheChildToStart_BeforeMeasuring()
    {
        var rig = new Rig();
        FinderBaseline baseline = await rig.Finder.CaptureAsync(Ct);

        await Collect(rig.Finder.RunAsync(["fake-tls", "split"], baseline, Ct));

        Assert.Equal([TimeSpan.FromMilliseconds(1500), TimeSpan.FromMilliseconds(1500)], rig.Delays);
    }

    [Fact]
    public async Task Run_ProbesAllFiveTelegramHosts_AndTheCloudflareDownload_WithTheSpecsLimits()
    {
        var rig = new Rig();
        FinderBaseline baseline = await rig.Finder.CaptureAsync(Ct);

        await Collect(rig.Finder.RunAsync(["split"], baseline, Ct));

        Assert.Equal(["kws1.web.telegram.org", "kws2.web.telegram.org", "kws3.web.telegram.org", "kws4.web.telegram.org", "kws5.web.telegram.org"],
                     rig.Probes.Handshakes.Select(h => h.Host).Order());
        Assert.All(rig.Probes.Handshakes, h => Assert.Equal((443, TimeSpan.FromSeconds(5)), (h.Port, h.Timeout)));
        var download = Assert.Single(rig.Probes.Downloads);
        Assert.Equal("https://speed.cloudflare.com/__down?bytes=65536", download.Url.ToString());
        Assert.Equal((65536L, TimeSpan.FromSeconds(10)), (download.Bytes, download.Timeout));
    }

    [Fact]
    public async Task Run_StartsTheSixProbesTogether_NotOneAfterAnother()
    {
        var rig = new Rig();
        int started = 0;
        var allSix = new TaskCompletionSource();
        rig.Probes.BeforeProbe = async ct =>
        {
            if (Interlocked.Increment(ref started) == 6) allSix.SetResult();
            await allSix.Task.WaitAsync(TimeSpan.FromSeconds(5), ct);   // a sequential run would wait here for ever
        };
        FinderBaseline baseline = await rig.Finder.CaptureAsync(Ct);

        List<FinderRow> rows = await Collect(rig.Finder.RunAsync(["split"], baseline, Ct));

        Assert.Single(rows);
        Assert.Equal(6, started);
    }

    [Fact]
    public async Task Run_AMedianOfTheHandshakeTimes_IsAMeasuredValue()
    {
        var rig = new Rig();
        rig.Probes.Outcomes["split"] = new Outcome(5, true, Ms150);   // 150, 160, 170, 180, 190 ms
        rig.Probes.Outcomes["fake-tls"] = new Outcome(4, true, Ms150);   // 150, 160, 170, 180: the lower middle
        FinderBaseline baseline = await rig.Finder.CaptureAsync(Ct);

        List<FinderRow> rows = await Collect(rig.Finder.RunAsync(["split", "fake-tls"], baseline, Ct));

        Assert.Equal(TimeSpan.FromMilliseconds(170), rows[0].Median);
        Assert.Equal(TimeSpan.FromMilliseconds(160), rows[1].Median);
    }

    [Fact]
    public async Task Run_ARefusedStrategy_IsAnErrorRow_AndTheNextOneIsStillTried()
    {
        var rig = new Rig();
        rig.Service.UseRefusal = "стратегия не прошла проверку: bad option";
        FinderBaseline baseline = await rig.Finder.CaptureAsync(Ct);
        rig.Probes.Outcomes["split"] = new Outcome(5, true, Ms150);

        var rows = new List<FinderRow>();
        await foreach (FinderRow r in rig.Finder.RunAsync(["fake-tls", "split"], baseline, Ct))
        {
            rows.Add(r);
            rig.Service.UseRefusal = null;   // only the first is refused
        }

        Assert.Equal("стратегия не прошла проверку: bad option", rows[0].Error);
        Assert.Equal(0, rows[0].TelegramOk);
        Assert.Null(rows[1].Error);
        Assert.Equal(5, rows[1].TelegramOk);
        Assert.Equal(5, rig.Probes.Handshakes.Count);   // the refused one was never probed
    }

    [Fact]
    public async Task Run_WithTheBypassOff_SwitchesItOnFirst_BecauseUseAloneWouldRunNothing()
    {
        var rig = new Rig();
        rig.Service.Enabled = false;
        FinderBaseline baseline = await rig.Finder.CaptureAsync(Ct);
        Assert.False(baseline.Enabled);

        await Collect(rig.Finder.RunAsync(["split"], baseline, Ct));

        Assert.Equal(["on", "use"], rig.Service.Sent.Select(r => r.Cmd).Where(c => c is "on" or "use"));
    }

    [Fact]
    public async Task Run_WhenTheBypassWillNotSwitchOn_StopsWithAMessage()
    {
        var rig = new Rig();
        rig.Service.Enabled = false;
        rig.Service.SwitchRefusal = "проверка параметров не прошла";
        FinderBaseline baseline = await rig.Finder.CaptureAsync(Ct);

        var ex = await Assert.ThrowsAsync<FinderException>(() => Collect(rig.Finder.RunAsync(["split"], baseline, Ct)));

        Assert.Equal("не удалось включить обход DPI: проверка параметров не прошла", ex.Message);
        Assert.Equal(0, rig.Service.Count("use"));
    }

    [Fact]
    public async Task Run_Cancelled_StopsAtOnce()
    {
        var rig = new Rig();
        FinderBaseline baseline = await rig.Finder.CaptureAsync(Ct);
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        rig.Probes.BeforeProbe = async ct =>
        {
            await cts.CancelAsync();
            ct.ThrowIfCancellationRequested();
        };

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Collect(rig.Finder.RunAsync(Rotation, baseline, cts.Token)));

        Assert.Equal(1, rig.Service.Count("use"));   // no further candidate was started
    }

    // ----- baseline, restore, apply -----

    [Fact]
    public async Task Capture_ReadsTheStrategyAndWhetherTheBypassIsOn()
    {
        var rig = new Rig();
        rig.Service.Strategy = "custom:my-isp";
        rig.Service.Enabled = false;

        FinderBaseline baseline = await rig.Finder.CaptureAsync(Ct);

        Assert.Equal(new FinderBaseline("custom:my-isp", false), baseline);
    }

    [Fact]
    public async Task Restore_PutsTheStrategyBack()
    {
        var rig = new Rig();
        rig.Service.Strategy = "split-stealth";
        FinderBaseline baseline = await rig.Finder.CaptureAsync(Ct);
        rig.Service.Strategy = "aggressive";   // where the run left it

        string? problem = await rig.Finder.RestoreAsync(baseline, Ct);

        Assert.Null(problem);
        Assert.Equal("split-stealth", rig.Service.Strategy);
        Assert.Equal(0, rig.Service.Count("off"));
    }

    [Fact]
    public async Task Restore_OfABypassThatWasOff_SwitchesItOffAgain()
    {
        var rig = new Rig();
        rig.Service.Enabled = false;
        FinderBaseline baseline = await rig.Finder.CaptureAsync(Ct);
        rig.Service.Enabled = true;   // the run switched it on

        await rig.Finder.RestoreAsync(baseline, Ct);

        Assert.False(rig.Service.Enabled);
        Assert.Equal(["use", "off"], rig.Service.Sent.Select(r => r.Cmd).Where(c => c is "use" or "off"));
    }

    [Fact]
    public async Task Restore_ThatTheServiceRefuses_ReturnsTheMessage()
    {
        var rig = new Rig();
        FinderBaseline baseline = await rig.Finder.CaptureAsync(Ct);
        rig.Service.UseRefusal = "нет такой стратегии";

        string? problem = await rig.Finder.RestoreAsync(baseline, Ct);

        Assert.Equal("не удалось вернуть стратегию auto: нет такой стратегии", problem);
    }

    [Fact]
    public async Task Restore_WithNoService_ReturnsTheMessage_InsteadOfThrowing()
    {
        var rig = new Rig();
        FinderBaseline baseline = await rig.Finder.CaptureAsync(Ct);
        rig.Service.Unavailable = true;

        string? problem = await rig.Finder.RestoreAsync(baseline, Ct);

        Assert.Equal("служба обхода DPI недоступна", problem);
    }

    [Fact]
    public async Task Apply_SendsUseForTheChosenStrategy()
    {
        var rig = new Rig();

        string? problem = await rig.Finder.ApplyAsync("split", Ct);

        Assert.Null(problem);
        Assert.Equal("split", rig.Service.Strategy);
        Assert.Equal("split", rig.Service.Sent.Last().Strategy);
    }

    [Fact]
    public async Task Apply_ThatTheServiceRefuses_ReturnsTheMessage()
    {
        var rig = new Rig();
        rig.Service.UseRefusal = "проверка параметров не прошла";

        Assert.Equal("проверка параметров не прошла", await rig.Finder.ApplyAsync("split", Ct));
    }
}

/// <summary>The dev-only probes the visual checks run on: they must behave like a run, or the screenshots would show something the app never does.</summary>
public class ScriptedProbesTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task TheScript_FollowsTheStrategyTheServiceIsOn_AndMirrorsTheSpecsExample()
    {
        var service = new FakeBypassService();
        var probes = new ScriptedProbes(service);
        var finder = new StrategyFinder(service, probes, probes, (_, _) => Task.CompletedTask);
        FinderBaseline baseline = await finder.CaptureAsync(Ct);

        var rows = new List<FinderRow>();
        await foreach (FinderRow r in finder.RunAsync(["fake-tls", "split", "split-stealth", "aggressive"], baseline, Ct)) rows.Add(r);

        Assert.Equal([5, 5, 3, 0], rows.Select(r => r.TelegramOk));
        Assert.Equal([true, true, false, false], rows.Select(r => r.CloudflareOk));
        Assert.Equal("split", StrategyFinder.Best(rows)!.StrategyId);
    }
}
