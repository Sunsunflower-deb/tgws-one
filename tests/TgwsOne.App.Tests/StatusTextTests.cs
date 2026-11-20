using TgwsOne.Core;
using TgwsOne.Core.Zapret;
using TgwsOne.Services;

namespace TgwsOne.Tests;

/// <summary>The spec's "Status model" tables, one case per row.</summary>
public class StatusTextTests
{
    // ----- Headline: the first matching row wins -----

    [Fact]
    public void Headline_StartFailed_IsRedWithTheReason()
    {
        var s = Samples.Snapshot(lastStart: new StartResult(StartOutcome.PortExhausted, 1443));

        Assert.Equal(("Прокси не запустился: порт 1443 занят, свободных портов рядом нет", Tone.Red), StatusText.Headline(s));
    }

    [Theory]
    [InlineData(StartOutcome.EngineMissing, null, "Прокси не запустился: не найден движок прокси")]
    [InlineData(StartOutcome.EngineExited, 3, "Прокси не запустился: движок завершился с кодом 3")]
    [InlineData(StartOutcome.EngineExited, null, "Прокси не запустился: движок не запустился")]
    [InlineData(StartOutcome.Timeout, null, "Прокси не запустился: порт 1443 не слушается")]
    public void Headline_StartFailed_NamesEachReason(StartOutcome outcome, int? exitCode, string expected)
    {
        var s = Samples.Snapshot(lastStart: new StartResult(outcome, 1443, ExitCode: exitCode));

        Assert.Equal((expected, Tone.Red), StatusText.Headline(s));
    }

    [Fact]
    public void Headline_StartFailed_BeatsEverythingElseWhileNothingListens()
    {
        // The first row wins: even with Telegram connections left over, a proxy that failed to start is a failure.
        var s = Samples.Snapshot(lastStart: new StartResult(StartOutcome.Timeout, 1443), connections: 2);

        Assert.Equal(Tone.Red, StatusText.Headline(s).Tone);
    }

    [Fact]
    public void Headline_AProxyThatCameUpSinceTheFailedStart_IsNotCalledFailed()
    {
        var s = Samples.Snapshot(Samples.ProxyListening(), lastStart: new StartResult(StartOutcome.Timeout, 1443), connections: 1);

        Assert.Equal(("Telegram работает через прокси", Tone.Green), StatusText.Headline(s));
    }

    [Theory]
    [InlineData(StartOutcome.Started)]
    [InlineData(StartOutcome.AlreadyRunning)]
    [InlineData(StartOutcome.PortSwitched)]
    public void Headline_SuccessfulStarts_AreNotFailures(StartOutcome outcome)
    {
        var s = Samples.Snapshot(Samples.ProxyListening(), lastStart: new StartResult(outcome, 1443), connections: 0);

        Assert.Equal(("Прокси готов — откройте ссылку в Telegram", Tone.Amber), StatusText.Headline(s));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(5)]
    public void Headline_ListeningWithConnections_IsGreen(int connections)
    {
        var s = Samples.Snapshot(Samples.ProxyListening(), connections: connections);

        Assert.Equal(("Telegram работает через прокси", Tone.Green), StatusText.Headline(s));
    }

    [Fact]
    public void Headline_ListeningWithoutConnections_IsAmber()
    {
        var s = Samples.Snapshot(Samples.ProxyListening(), connections: 0);

        Assert.Equal(("Прокси готов — откройте ссылку в Telegram", Tone.Amber), StatusText.Headline(s));
    }

    [Fact]
    public void Headline_Stopped_IsGrey()
    {
        Assert.Equal(("Отключено", Tone.Grey), StatusText.Headline(Samples.Snapshot()));
    }

    [Fact]
    public void Headline_StartedButNotListeningYet_IsStillGrey()
    {
        var s = Samples.Snapshot(new ProxyStatus(Running: true, Listening: false, Port: 1443, PortBusyByOther: false, Pid: 4242));

        Assert.Equal(("Отключено", Tone.Grey), StatusText.Headline(s));
    }

    [Fact]
    public void Headline_BeforeTheFirstPoll_IsGrey()
    {
        Assert.Equal(("Отключено", Tone.Grey), StatusText.Headline(AppSnapshot.Empty));
    }

    // ----- Bypass line -----

    private static (string Text, Tone Tone) Line(ZapretStatus? bypass, bool reachable = true)
        => StatusText.BypassLine(Samples.Snapshot(bypass: bypass, reachable: reachable));

    [Fact]
    public void BypassLine_ServiceUnreachable_IsGreyAndAsksForAnAdministrator()
    {
        Assert.Equal(("не установлен — нужен администратор", Tone.Grey), Line(null, reachable: false));
    }

    [Fact]
    public void BypassLine_Disabled()
    {
        Assert.Equal(("выключен", Tone.Grey), Line(Samples.Bypass("disabled")));
    }

    [Fact]
    public void BypassLine_RunningAuto_NamesTheFirstHostAndCountsTheRest()
    {
        var four = Samples.Bypass(auto: [
            Samples.Host("kws2.web.telegram.org", "split"), Samples.Host("kws4.web.telegram.org", "fake-tls"),
            Samples.Host("kws1.web.telegram.org", "split"), Samples.Host("kws5.web.telegram.org", "aggressive")]);

        Assert.Equal(("включён · Авто · kws2 → split, ещё 3", Tone.Green), Line(four));
    }

    [Fact]
    public void BypassLine_RunningAuto_WithOneHost_HasNoCount()
    {
        var one = Samples.Bypass(auto: [Samples.Host("kws2.web.telegram.org", "split-stealth", failures: 1)]);

        Assert.Equal(("включён · Авто · kws2 → split-stealth", Tone.Green), Line(one));
    }

    [Fact]
    public void BypassLine_RunningAuto_WithoutData_ShowsJustAuto()
    {
        Assert.Equal(("включён · Авто", Tone.Green), Line(Samples.Bypass()));
    }

    [Fact]
    public void BypassLine_RunningAuto_ShowsAnyOtherHostWhole()
    {
        var other = Samples.Bypass(auto: [Samples.Host("telegram.org", "fake-tls")]);

        Assert.Equal(("включён · Авто · telegram.org → fake-tls", Tone.Green), Line(other));
    }

    [Theory]
    [InlineData("split", "включён · split")]
    [InlineData("fake-tls", "включён · fake-tls")]
    [InlineData("split-stealth", "включён · split-stealth")]
    [InlineData("aggressive", "включён · aggressive")]
    [InlineData("custom:my-isp", "включён · my-isp (своя)")]
    public void BypassLine_RunningAnotherStrategy_ShowsItsDisplayName(string strategy, string expected)
    {
        Assert.Equal((expected, Tone.Green), Line(Samples.Bypass(strategy: strategy)));
    }

    [Fact]
    public void BypassLine_Starting_IsAmber()
    {
        Assert.Equal(("запускается…", Tone.Amber), Line(Samples.Bypass("starting")));
    }

    [Fact]
    public void BypassLine_Backoff_IsAmber()
    {
        Assert.Equal(("перезапуск после сбоя…", Tone.Amber), Line(Samples.Bypass("backoff")));
    }

    [Fact]
    public void BypassLine_Error_ShowsOnlyTheFirstLineOfTheMessage()
    {
        var error = Samples.Bypass("error", lastError: "параметры не подходят\nвторая строка\nтретья");

        Assert.Equal(("ошибка: параметры не подходят", Tone.Red), Line(error));
    }

    [Fact]
    public void BypassLine_ErrorWithoutAMessage_JustSaysSo()
    {
        Assert.Equal(("ошибка", Tone.Red), Line(Samples.Bypass("error")));
    }

    [Fact]
    public void BypassLine_ReachableButNoStatus_IsGrey()
    {
        Assert.Equal(("нет данных", Tone.Grey), Line(null));
    }

    // ----- Display names -----

    [Theory]
    [InlineData("auto", "Авто (рекомендуется)")]
    [InlineData("fake-tls", "fake-tls")]            // the technical names are not translated
    [InlineData("split", "split")]
    [InlineData("split-stealth", "split-stealth")]
    [InlineData("aggressive", "aggressive")]
    [InlineData("custom:x", "x (своя)")]
    [InlineData("custom:my-isp", "my-isp (своя)")]
    [InlineData("something-new", "something-new")]
    [InlineData("custom:", "custom:")]
    public void DisplayName_CoversEveryId(string id, string expected)
    {
        Assert.Equal(expected, StatusText.DisplayName(id));
    }
}
