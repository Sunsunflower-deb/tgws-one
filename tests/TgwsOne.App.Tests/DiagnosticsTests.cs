using TgwsOne.Core;
using TgwsOne.Core.Zapret;
using TgwsOne.Services;

namespace TgwsOne.Tests;

/// <summary>The spec's "Диагностика" checks and its verdict table, one case per row.</summary>
public class DiagnosticsTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>Everything in order unless a test says otherwise: engine there, proxy listening with Telegram connected, bypass on (Авто), network fine.</summary>
    private sealed class Rig
    {
        public FakeProxyControl Proxy { get; } = new();
        public FakeBypassService Service { get; } = new();
        public FakeNetInfo Net { get; } = new();
        public FakeAppSettings Settings { get; } = new();
        public FixedProbes Probes { get; } = new();
        public bool Engine { get; set; } = true;
        public Diagnostics Diagnostics { get; }

        public Rig()
        {
            Proxy.Status = Samples.ProxyListening(1443);
            Net.Set(Samples.Tcp(1443, 50001));
            Diagnostics = new Diagnostics(Proxy, Service, Net, Settings, Probes, Probes, Probes, () => Engine);
        }

        public async Task<List<CheckResult>> RunAsync()
        {
            var results = new List<CheckResult>();
            await foreach (CheckResult r in Diagnostics.RunAsync(Ct)) results.Add(r);
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
    public async Task AHealthySystem_GivesSevenOkLines_InTheSpecsOrder()
    {
        var rig = new Rig();

        List<CheckResult> results = await rig.RunAsync();

        Assert.Equal(["Движок прокси на месте", "Прокси слушает 127.0.0.1:1443", "Telegram подключён к прокси", "Служба обхода: включена, Авто",
                      "TCP до серверов Telegram: 5/5", "TLS до серверов Telegram: 5/5", "Cloudflare: 64 КБ за 0,8 с"],
                     results.Select(r => r.Title));
        Assert.All(results, r => Assert.Equal(CheckState.Ok, r.State));
    }

    [Fact]
    public async Task TheSpecsExample_ProviderCutsTelegram_ShowsAWarningAndAFailure()
    {
        var rig = new Rig();
        rig.Net.Set();   // Telegram is not connected to the proxy
        rig.Probes.TlsOk = 0;

        List<CheckResult> results = await rig.RunAsync();

        Assert.Equal((CheckState.Warn, "Telegram не подключён к прокси"), (results[2].State, results[2].Title));
        Assert.Equal((CheckState.Ok, "TCP до серверов Telegram: 5/5"), (results[4].State, results[4].Title));
        Assert.Equal((CheckState.Fail, "TLS до серверов Telegram: 0/5"), (results[5].State, results[5].Title));
    }

    [Theory]
    [InlineData(5, "Ok")]
    [InlineData(3, "Warn")]
    [InlineData(1, "Warn")]
    [InlineData(0, "Fail")]
    public async Task TheTelegramCounts_AreOkWarnOrFail(int ok, string expected)
    {
        var rig = new Rig();
        rig.Probes.TlsOk = ok;
        rig.Probes.TcpOk = Math.Max(ok, 1);   // keep TCP up: with none at all the TLS line is skipped

        List<CheckResult> results = await rig.RunAsync();

        Assert.Equal($"TLS до серверов Telegram: {ok}/5", results[5].Title);
        Assert.Equal(Enum.Parse<CheckState>(expected), results[5].State);
    }

    [Fact]
    public async Task WithoutAnyTcp_TheTlsLineIsSkipped_AsThereIsNothingToShakeHandsWith()
    {
        var rig = new Rig();
        rig.Probes.TcpOk = 0;

        List<CheckResult> results = await rig.RunAsync();

        Assert.Equal((CheckState.Fail, "TCP до серверов Telegram: 0/5"), (results[4].State, results[4].Title));
        Assert.Equal(CheckState.Skipped, results[5].State);
        Assert.Equal("до серверов нет даже TCP", results[5].Detail);
    }

    [Fact]
    public async Task TheProxyLine_SaysWhatIsWrong()
    {
        var rig = new Rig();

        rig.Proxy.Status = Samples.ProxyStopped;
        CheckResult stopped = (await rig.RunAsync())[1];
        Assert.Equal((CheckState.Fail, "Прокси не запущен"), (stopped.State, stopped.Title));

        rig.Proxy.Status = new ProxyStatus(false, false, 1443, PortBusyByOther: true, null);
        CheckResult busy = (await rig.RunAsync())[1];
        Assert.Equal((CheckState.Fail, "Порт 1443 занят другим приложением"), (busy.State, busy.Title));

        rig.Proxy.Status = new ProxyStatus(true, false, 1443, false, 4242);
        CheckResult starting = (await rig.RunAsync())[1];
        Assert.Equal(CheckState.Warn, starting.State);
    }

    [Fact]
    public async Task TelegramsConnection_IsSkipped_WhenThereIsNoProxyToConnectTo()
    {
        var rig = new Rig();
        rig.Proxy.Status = Samples.ProxyStopped;
        rig.Net.Set(Samples.Tcp(1443, 50001));   // stale rows in the table must not count

        CheckResult line = (await rig.RunAsync())[2];

        Assert.Equal(CheckState.Skipped, line.State);
        Assert.Equal("прокси не запущен", line.Detail);
    }

    [Fact]
    public async Task TheEngineLine_FailsWhenTheEngineIsMissing()
    {
        var rig = new Rig { Engine = false };

        CheckResult line = (await rig.RunAsync())[0];

        Assert.Equal((CheckState.Fail, "Движок прокси не найден"), (line.State, line.Title));
    }

    [Fact]
    public async Task TheBypassLine_FollowsTheServiceState()
    {
        var rig = new Rig();

        rig.Service.Strategy = "split";
        Assert.Equal("Служба обхода: включена, split", (await rig.RunAsync())[3].Title);

        rig.Service.Enabled = false;
        CheckResult off = (await rig.RunAsync())[3];
        Assert.Equal((CheckState.Warn, "Служба обхода: выключена"), (off.State, off.Title));

        rig.Service.Enabled = true;
        rig.Service.State = "error";
        rig.Service.LastError = "параметры не подходят\nподробности";
        CheckResult error = (await rig.RunAsync())[3];
        Assert.Equal((CheckState.Fail, "Служба обхода: ошибка — параметры не подходят"), (error.State, error.Title));

        rig.Service.State = "backoff";
        Assert.Equal(CheckState.Warn, (await rig.RunAsync())[3].State);

        rig.Service.Unavailable = true;
        CheckResult gone = (await rig.RunAsync())[3];
        Assert.Equal((CheckState.Warn, "Служба обхода: не установлена"), (gone.State, gone.Title));
    }

    [Fact]
    public async Task TheCloudflareLine_ShowsTheTimeInRussianNotation_OrTheFailure()
    {
        var rig = new Rig();
        rig.Probes.CloudflareTime = TimeSpan.FromMilliseconds(1250);
        Assert.Equal("Cloudflare: 64 КБ за 1,3 с", (await rig.RunAsync())[6].Title);   // a decimal comma, rounded to a tenth

        rig.Probes.CloudflareOk = false;
        CheckResult failed = (await rig.RunAsync())[6];
        Assert.Equal((CheckState.Fail, "Cloudflare: 64 КБ не получены за 10 с"), (failed.State, failed.Title));
    }

    [Fact]
    public async Task TheNetworkProbes_UseTheSpecsHostsPortsAndLimits()
    {
        var rig = new Rig();

        await rig.RunAsync();

        string[] hosts = ["kws1.web.telegram.org", "kws2.web.telegram.org", "kws3.web.telegram.org", "kws4.web.telegram.org", "kws5.web.telegram.org"];
        foreach (string kind in new[] { "tcp", "tls" })
        {
            var calls = rig.Probes.Calls.Where(c => c.Kind == kind).ToList();
            Assert.Equal(hosts, calls.Select(c => c.Host).Order());
            Assert.All(calls, c => Assert.Equal((443, TimeSpan.FromSeconds(5)), (c.Port, c.Timeout)));
        }
        Assert.Equal(("https://speed.cloudflare.com/__down?bytes=65536", 65536L, TimeSpan.FromSeconds(10)),
                     (rig.Probes.LastDownload!.Value.Url.ToString(), rig.Probes.LastDownload.Value.Bytes, rig.Probes.LastDownload.Value.Timeout));
    }

    [Fact]
    public async Task AllElevenNetworkProbes_StartTogether_SoARunTakesAsLongAsTheSlowestOne()
    {
        var rig = new Rig();
        int started = 0;
        var all = new TaskCompletionSource();
        rig.Probes.BeforeProbe = async ct =>
        {
            if (Interlocked.Increment(ref started) == 11) all.SetResult();
            await all.Task.WaitAsync(TimeSpan.FromSeconds(5), ct);   // a sequential run would wait here for ever
        };

        List<CheckResult> results = await rig.RunAsync();

        Assert.Equal(7, results.Count);
        Assert.Equal(11, started);
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
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        rig.Probes.BeforeProbe = async ct =>
        {
            await cts.CancelAsync();
            ct.ThrowIfCancellationRequested();
        };

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
        {
            await foreach (CheckResult _ in rig.Diagnostics.RunAsync(cts.Token)) { }
        });

        Assert.Throws<InvalidOperationException>(() => rig.Diagnostics.Decide());   // the earlier run's verdict is not kept as if it were current
    }

    // ----- the verdict table, one case per row -----

    [Fact]
    public async Task Verdict_1_NoEngine()
    {
        var rig = new Rig { Engine = false };

        Assert.Equal(new Verdict("Повреждена установка: нет движка прокси. Переустановите TgwsOne.", VerdictAction.None), await rig.VerdictAsync());
    }

    [Fact]
    public async Task Verdict_2_ThePortIsHeldByAnotherApp()
    {
        var rig = new Rig();
        rig.Proxy.Status = new ProxyStatus(false, false, 1443, PortBusyByOther: true, null);

        Assert.Equal(new Verdict("Порт 1443 занят другим приложением.", VerdictAction.ChangePort), await rig.VerdictAsync());
    }

    [Fact]
    public async Task Verdict_3_TheProxyIsNotRunning()
    {
        var rig = new Rig();
        rig.Proxy.Status = Samples.ProxyStopped;

        Assert.Equal(new Verdict("Прокси выключен.", VerdictAction.Connect), await rig.VerdictAsync());
    }

    [Fact]
    public async Task Verdict_4_NoTcpToTelegram()
    {
        var rig = new Rig();
        rig.Probes.TcpOk = 0;
        rig.Probes.TlsOk = 0;

        Assert.Equal(new Verdict("Нет связи с серверами Telegram — проверьте интернет; если не поможет, нужен VPN.", VerdictAction.None), await rig.VerdictAsync());
    }

    [Fact]
    public async Task Verdict_5_TlsCut_AndNoServiceInstalled()
    {
        var rig = new Rig();
        rig.Probes.TlsOk = 2;
        rig.Service.Unavailable = true;

        Assert.Equal(new Verdict("Провайдер режет Telegram, а обход DPI не установлен — попросите администратора переустановить TgwsOne.", VerdictAction.None),
                     await rig.VerdictAsync());
    }

    [Fact]
    public async Task Verdict_6_TlsCut_AndTheBypassIsOff()
    {
        var rig = new Rig();
        rig.Probes.TlsOk = 0;
        rig.Service.Enabled = false;

        Assert.Equal(new Verdict("Провайдер режет Telegram. Включите обход DPI.", VerdictAction.EnableBypass), await rig.VerdictAsync());
    }

    [Fact]
    public async Task Verdict_7_TlsCut_AndTheBypassCannotCope()
    {
        var rig = new Rig();
        rig.Probes.TlsOk = 0;

        Assert.Equal(new Verdict("Провайдер режет Telegram, обход не справляется.", VerdictAction.FindStrategy), await rig.VerdictAsync());
    }

    [Fact]
    public async Task Verdict_8_CloudflareFails()
    {
        var rig = new Rig();
        rig.Probes.CloudflareOk = false;

        Assert.Equal(new Verdict("Cloudflare работает с перебоями — резервный канал прокси может тормозить.", VerdictAction.FindStrategy), await rig.VerdictAsync());
    }

    [Fact]
    public async Task Verdict_9_TelegramIsNotConnected()
    {
        var rig = new Rig();
        rig.Net.Set();

        Assert.Equal(new Verdict("Прокси работает, но Telegram к нему не подключён.", VerdictAction.OpenInTelegram), await rig.VerdictAsync());
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
        // Everything is wrong at once: the engine row is the one that is said.
        var rig = new Rig { Engine = false };
        rig.Proxy.Status = Samples.ProxyStopped;
        rig.Probes.TcpOk = 0;
        rig.Probes.CloudflareOk = false;
        rig.Net.Set();
        Assert.Equal("Повреждена установка: нет движка прокси. Переустановите TgwsOne.", (await rig.VerdictAsync()).Text);

        // Engine back: now the proxy being off outranks the network.
        rig.Engine = true;
        Assert.Equal("Прокси выключен.", (await rig.VerdictAsync()).Text);

        // Proxy back: a dead network outranks a cut handshake and the rest.
        rig.Proxy.Status = Samples.ProxyListening();
        rig.Net.Set(Samples.Tcp(1443, 50001));
        Assert.StartsWith("Нет связи с серверами Telegram", (await rig.VerdictAsync()).Text);

        // TCP back: a cut handshake outranks Cloudflare and the missing connection.
        rig.Probes.TcpOk = 5;
        rig.Probes.TlsOk = 0;
        rig.Net.Set();
        Assert.Equal("Провайдер режет Telegram, обход не справляется.", (await rig.VerdictAsync()).Text);

        // TLS back: Cloudflare outranks the missing connection.
        rig.Probes.TlsOk = 5;
        Assert.StartsWith("Cloudflare работает с перебоями", (await rig.VerdictAsync()).Text);
    }

    [Fact]
    public async Task AProxyThatIsListening_IsNeverReportedAsBusyByAnotherApp()
    {
        var rig = new Rig();
        rig.Proxy.Status = new ProxyStatus(true, true, 1443, PortBusyByOther: true, 4242);   // nonsense input: listening wins

        Assert.Equal("Всё в порядке.", (await rig.VerdictAsync()).Text);
    }
}
