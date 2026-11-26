using TgwsOne.Engine;

namespace TgwsOne.Mobile.Tests;

/// <summary>The mobile diagnostics checks and the verdict table, one case per row.</summary>
public class DiagnosticsTests
{
    private static CancellationToken Ct => CancellationToken.None;

    /// <summary>Everything in order unless a test says otherwise: engine there, proxy listening with Telegram attached, bypass on (Авто), network fine.</summary>
    private sealed class Rig
    {
        public FakeEngineControl Engine { get; } = new();
        public FakeDesyncControl Desync { get; } = new();
        public DiagnosticProbes Probes { get; } = new();
        public bool EngineExists { get; set; } = true;
        public long Sessions { get; set; } = 1;
        public Diagnostics Diagnostics { get; }

        public Rig()
        {
            Diagnostics = new Diagnostics(Engine, Desync, Probes, () => EngineExists, () => Sessions);
        }

        public async Task<List<CheckResult>> RunAsync()
        {
            var results = new List<CheckResult>();
            await foreach (CheckResult result in Diagnostics.RunAsync(Ct)) results.Add(result);
            return results;
        }

        public async Task<Verdict> VerdictAsync()
        {
            await RunAsync();
            return Diagnostics.Decide();
        }
    }

    // ----- the checks -----

    [Fact]
    public async Task AHealthySystem_GivesEightOkLines_InTheSpecsOrder()
    {
        var rig = new Rig();

        List<CheckResult> results = await rig.RunAsync();

        Assert.Equal([
            "Движок прокси на месте",
            "Прокси слушает 127.0.0.1:1443",
            "Telegram подключён к прокси",
            "Обход: включён, Авто",
            "TCP до серверов Telegram: 5/5",
            "TLS до серверов Telegram: 5/5",
            "TLS до серверов Telegram через обход: 5/5",
            "Cloudflare: 64 КБ за 0,8 с",
        ], results.Select(result => result.Title));
        Assert.All(results, result => Assert.Equal(CheckState.Ok, result.State));
    }

    [Fact]
    public async Task TheEngineLine_FailsWhenTheEngineIsMissing()
    {
        var rig = new Rig { EngineExists = false };

        CheckResult line = (await rig.RunAsync())[0];

        Assert.Equal((CheckState.Fail, "Движок прокси не найден"), (line.State, line.Title));
    }

    [Fact]
    public async Task TheProxyLine_SaysWhatIsWrong()
    {
        var rig = new Rig();

        rig.Engine.Status = new EngineProxyStatus(false, false, 1443, PortBusyByOther: false, Pid: null);
        CheckResult stopped = (await rig.RunAsync())[1];
        Assert.Equal((CheckState.Fail, "Прокси не запущен"), (stopped.State, stopped.Title));

        rig.Engine.Status = new EngineProxyStatus(false, false, 1443, PortBusyByOther: true, Pid: null);
        CheckResult busy = (await rig.RunAsync())[1];
        Assert.Equal((CheckState.Fail, "Порт 1443 занят другим приложением"), (busy.State, busy.Title));

        rig.Engine.Status = new EngineProxyStatus(true, false, 1443, false, 4242);
        CheckResult starting = (await rig.RunAsync())[1];
        Assert.Equal(CheckState.Warn, starting.State);
    }

    [Fact]
    public async Task TelegramsConnection_FollowsTheEngineSessionCount_AndIsSkippedWithoutAProxy()
    {
        var rig = new Rig();
        rig.Sessions = 0;
        CheckResult none = (await rig.RunAsync())[2];
        Assert.Equal((CheckState.Warn, "Telegram не подключён к прокси"), (none.State, none.Title));

        rig.Sessions = 3;
        Assert.Equal(CheckState.Ok, (await rig.RunAsync())[2].State);

        rig.Engine.Status = new EngineProxyStatus(false, false, 1443, false, null);
        CheckResult skipped = (await rig.RunAsync())[2];
        Assert.Equal(CheckState.Skipped, skipped.State);
        Assert.Equal("прокси не запущен", skipped.Detail);
    }

    [Fact]
    public async Task TheBypassLine_ShowsTheStrategyAndTheAutoState()
    {
        var rig = new Rig();

        Assert.Equal("Обход: включён, Авто", (await rig.RunAsync())[3].Title);

        rig.Desync.Baseline = new FinderBaseline("split-sni", true);
        Assert.Equal("Обход: включён, Разрез после имени", (await rig.RunAsync())[3].Title);

        rig.Desync.Baseline = new FinderBaseline("auto", false);
        CheckResult off = (await rig.RunAsync())[3];
        Assert.Equal((CheckState.Warn, "Обход: выключен"), (off.State, off.Title));
    }

    [Theory]
    [InlineData(5, "Ok")]
    [InlineData(3, "Warn")]
    [InlineData(1, "Warn")]
    [InlineData(0, "Fail")]
    public async Task TheDirectTlsCounts_AreOkWarnOrFail(int ok, string expected)
    {
        var rig = new Rig();
        rig.Probes.TlsOk = ok;
        rig.Probes.TcpOk = Math.Max(ok, 1);

        List<CheckResult> results = await rig.RunAsync();

        Assert.Equal($"TLS до серверов Telegram: {ok}/5", results[5].Title);
        Assert.Equal(Enum.Parse<CheckState>(expected), results[5].State);
    }

    [Theory]
    [InlineData(5, "Ok")]
    [InlineData(3, "Warn")]
    [InlineData(0, "Fail")]
    public async Task TheBypassTlsCounts_AreOkWarnOrFail(int ok, string expected)
    {
        var rig = new Rig();
        rig.Probes.BypassTlsOk = ok;

        List<CheckResult> results = await rig.RunAsync();

        Assert.Equal($"TLS до серверов Telegram через обход: {ok}/5", results[6].Title);
        Assert.Equal(Enum.Parse<CheckState>(expected), results[6].State);
    }

    [Fact]
    public async Task WithoutAnyTcp_BothTlsLinesAreSkipped()
    {
        var rig = new Rig();
        rig.Probes.TcpOk = 0;

        List<CheckResult> results = await rig.RunAsync();

        Assert.Equal((CheckState.Fail, "TCP до серверов Telegram: 0/5"), (results[4].State, results[4].Title));
        Assert.Equal(CheckState.Skipped, results[5].State);
        Assert.Equal("до серверов нет даже TCP", results[5].Detail);
        Assert.Equal(CheckState.Skipped, results[6].State);
        Assert.Equal("до серверов нет даже TCP", results[6].Detail);
    }

    [Fact]
    public async Task TheCloudflareLine_ShowsTheTimeInRussianNotation_OrTheFailure()
    {
        var rig = new Rig();
        rig.Probes.CloudflareTime = TimeSpan.FromMilliseconds(1250);
        Assert.Equal("Cloudflare: 64 КБ за 1,3 с", (await rig.RunAsync())[7].Title);

        rig.Probes.CloudflareOk = false;
        CheckResult failed = (await rig.RunAsync())[7];
        Assert.Equal((CheckState.Fail, "Cloudflare: 64 КБ не получены за 10 с"), (failed.State, failed.Title));
    }

    [Fact]
    public async Task TheNetworkProbes_UseTheSpecsHostsPortsAndLimits()
    {
        var rig = new Rig();

        await rig.RunAsync();

        string[] hosts = ["kws1.web.telegram.org", "kws2.web.telegram.org", "kws3.web.telegram.org",
                          "kws4.web.telegram.org", "kws5.web.telegram.org"];
        foreach (string kind in new[] { "tcp", "tls", "bypass" })
        {
            var calls = rig.Probes.Calls.Where(call => call.Kind == kind).ToList();
            Assert.Equal(hosts, calls.Select(call => call.Host).Order());
            Assert.All(calls, call => Assert.Equal((443, TimeSpan.FromSeconds(5)), (call.Port, call.Timeout)));
        }
        Assert.All(rig.Probes.Calls.Where(call => call.Kind == "bypass"), call => Assert.Equal("ws", call.Route));
        Assert.Equal(("https://speed.cloudflare.com/__down?bytes=65536", 65536L, TimeSpan.FromSeconds(10)),
                     (rig.Probes.LastDownload!.Value.Url.ToString(), rig.Probes.LastDownload.Value.Bytes,
                      rig.Probes.LastDownload.Value.Timeout));
    }

    [Fact]
    public async Task AllSixteenNetworkProbes_StartTogether_SoARunTakesAsLongAsTheSlowestOne()
    {
        var rig = new Rig();
        int started = 0;
        var all = new TaskCompletionSource();
        rig.Probes.BeforeProbe = async ct =>
        {
            if (Interlocked.Increment(ref started) == 16) all.SetResult();
            await all.Task.WaitAsync(TimeSpan.FromSeconds(5), ct);
        };

        List<CheckResult> results = await rig.RunAsync();

        Assert.Equal(8, results.Count);
        Assert.Equal(16, started);
    }

    [Fact]
    public async Task Decide_BeforeAnyRun_IsAnError()
    {
        var rig = new Rig();

        Assert.Throws<InvalidOperationException>(() => rig.Diagnostics.Decide());
        await rig.RunAsync();
        Assert.Equal("Всё в порядке.", rig.Diagnostics.Decide().Text);
    }

    [Fact]
    public async Task ACancelledRun_LeavesNoVerdictBehind()
    {
        var rig = new Rig();
        await rig.RunAsync();
        using var cts = new CancellationTokenSource();
        rig.Probes.BeforeProbe = async ct =>
        {
            await cts.CancelAsync();
            ct.ThrowIfCancellationRequested();
        };

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
        {
            await foreach (CheckResult _ in rig.Diagnostics.RunAsync(cts.Token)) { }
        });

        Assert.Throws<InvalidOperationException>(() => rig.Diagnostics.Decide());
    }

    // ----- the verdict table, one case per row -----

    [Fact]
    public async Task Verdict_1_NoEngine()
    {
        var rig = new Rig { EngineExists = false };

        Assert.Equal(new Verdict("Повреждена установка: нет движка прокси. Переустановите TgwsOne.", VerdictAction.None),
                     await rig.VerdictAsync());
    }

    [Fact]
    public async Task Verdict_2_ThePortIsHeldByAnotherApp()
    {
        var rig = new Rig();
        rig.Engine.Status = new EngineProxyStatus(false, false, 1443, PortBusyByOther: true, Pid: null);

        Assert.Equal(new Verdict("Порт 1443 занят другим приложением.", VerdictAction.ChangePort), await rig.VerdictAsync());
    }

    [Fact]
    public async Task Verdict_3_TheProxyIsNotRunning()
    {
        var rig = new Rig();
        rig.Engine.Status = new EngineProxyStatus(false, false, 1443, false, null);

        Assert.Equal(new Verdict("Прокси выключен.", VerdictAction.Connect), await rig.VerdictAsync());
    }

    [Fact]
    public async Task Verdict_4_NoTcpToTelegram()
    {
        var rig = new Rig();
        rig.Probes.TcpOk = 0;
        rig.Probes.TlsOk = 0;
        rig.Probes.BypassTlsOk = 0;

        Assert.Equal(new Verdict("Нет связи с серверами Telegram — проверьте интернет; если не поможет, нужен VPN.", VerdictAction.None),
                     await rig.VerdictAsync());
    }

    [Fact]
    public async Task Verdict_5_TlsCut_AndTheBypassIsOff()
    {
        var rig = new Rig();
        rig.Probes.TlsOk = 2;
        rig.Desync.Baseline = new FinderBaseline("auto", false);

        Assert.Equal(new Verdict("TLS режется, обход выключен.", VerdictAction.EnableBypass), await rig.VerdictAsync());
    }

    [Fact]
    public async Task Verdict_6_TlsCut_AndTheCurrentStrategyDoesNotHelp()
    {
        var rig = new Rig();
        rig.Probes.TlsOk = 2;
        rig.Probes.BypassTlsOk = 1;

        Assert.Equal(new Verdict("TLS режется, текущая стратегия не помогает.", VerdictAction.FindStrategy),
                     await rig.VerdictAsync());
    }

    [Fact]
    public async Task Verdict_7_TlsCut_ButTheBypassGetsEverythingThrough_IsNotAVerdict()
    {
        // Direct TLS is cut, but the bypass carries all five: the cut is handled, so later rows decide.
        var rig = new Rig();
        rig.Probes.TlsOk = 0;
        rig.Probes.BypassTlsOk = 5;

        Assert.Equal("Всё в порядке.", (await rig.VerdictAsync()).Text);

        rig.Probes.CloudflareOk = false;
        Assert.Equal("Cloudflare работает с перебоями — резервный канал прокси может тормозить.",
                     (await rig.VerdictAsync()).Text);
    }

    [Fact]
    public async Task Verdict_8_CloudflareFails()
    {
        var rig = new Rig();
        rig.Probes.CloudflareOk = false;

        Assert.Equal(new Verdict("Cloudflare работает с перебоями — резервный канал прокси может тормозить.", VerdictAction.FindStrategy),
                     await rig.VerdictAsync());
    }

    [Fact]
    public async Task Verdict_9_TelegramIsNotConnected()
    {
        var rig = new Rig();
        rig.Sessions = 0;

        Assert.Equal(new Verdict("Прокси работает, но Telegram к нему не подключён.", VerdictAction.OpenInTelegram),
                     await rig.VerdictAsync());
    }

    [Fact]
    public async Task Verdict_10_EverythingIsFine()
    {
        var rig = new Rig();

        Assert.Equal(new Verdict("Всё в порядке.", VerdictAction.None), await rig.VerdictAsync());
    }

    // ----- the first matching row wins -----

    [Fact]
    public async Task TheEarlierRow_BeatsALaterOne()
    {
        var rig = new Rig { EngineExists = false };
        rig.Engine.Status = new EngineProxyStatus(false, false, 1443, false, null);
        rig.Probes.TcpOk = 0;
        rig.Probes.CloudflareOk = false;
        rig.Sessions = 0;
        Assert.Equal("Повреждена установка: нет движка прокси. Переустановите TgwsOne.", (await rig.VerdictAsync()).Text);

        rig.EngineExists = true;
        Assert.Equal("Прокси выключен.", (await rig.VerdictAsync()).Text);

        rig.Engine.Status = new EngineProxyStatus(true, true, 1443, false, 4242);
        Assert.StartsWith("Нет связи с серверами Telegram", (await rig.VerdictAsync()).Text);

        rig.Probes.TcpOk = 5;
        rig.Probes.TlsOk = 0;
        rig.Probes.BypassTlsOk = 0;
        rig.Sessions = 0;
        Assert.Equal("TLS режется, текущая стратегия не помогает.", (await rig.VerdictAsync()).Text);

        rig.Probes.TlsOk = 5;
        Assert.StartsWith("Cloudflare работает с перебоями", (await rig.VerdictAsync()).Text);
    }

    [Fact]
    public async Task AProxyThatIsListening_IsNeverReportedAsBusyByAnotherApp()
    {
        var rig = new Rig();
        rig.Engine.Status = new EngineProxyStatus(true, true, 1443, PortBusyByOther: true, 4242);

        Assert.Equal("Всё в порядке.", (await rig.VerdictAsync()).Text);
    }
}
