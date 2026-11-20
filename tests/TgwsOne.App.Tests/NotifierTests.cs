using TgwsOne.Core;
using TgwsOne.Core.Zapret;
using TgwsOne.Services;

namespace TgwsOne.Tests;

internal sealed class RecordingSink : INotificationSink
{
    private readonly List<(string Title, string Body)> _shown = [];

    public Exception? Throws { get; set; }

    public IReadOnlyList<(string Title, string Body)> Shown { get { lock (_shown) return [.. _shown]; } }

    public Task ShowAsync(string title, string body, CancellationToken ct = default)
    {
        lock (_shown) _shown.Add((title, body));
        return Throws is { } ex ? Task.FromException(ex) : Task.CompletedTask;
    }
}

/// <summary>The spec's notification rules: transitions only, nothing for what the user did, one per kind per 30 s.</summary>
public class NotifierTests
{
    private static readonly DateTimeOffset Start = new(2026, 11, 20, 10, 0, 0, TimeSpan.Zero);

    private static ProxyStatus Listening(int pid = 4242, int port = 1443) => new(Running: true, Listening: true, Port: port, PortBusyByOther: false, Pid: pid);

    private static AppSnapshot Snap(ProxyStatus? proxy = null, ZapretStatus? bypass = null, StartResult? lastStart = null)
        => Samples.Snapshot(proxy ?? Samples.ProxyStopped, lastStart, bypass: bypass, reachable: bypass != null);

    private sealed class Rig
    {
        public RecordingSink Sink { get; } = new();
        public FixedClock Clock { get; } = new(Start);
        public Notifier Notifier { get; }

        /// <param name="first">The snapshot the app starts from (what the monitor had when the notifier began to follow it).</param>
        public Rig(AppSnapshot? first = null)
        {
            Notifier = new Notifier(Sink, Clock);
            Notifier.OnSnapshot(first ?? Snap());   // the baseline: nothing is said about it
        }

        public void Feed(AppSnapshot s) => Notifier.OnSnapshot(s);
        public IReadOnlyList<string> Bodies => [.. Sink.Shown.Select(n => n.Body)];
    }

    // ----- the proxy -----

    [Fact]
    public void TheFirstSnapshot_IsOnlyABaseline()
    {
        var rig = new Rig(Snap(Listening()));

        Assert.Empty(rig.Sink.Shown);
    }

    [Fact]
    public void AProxyThatComesUp_IsNotNews()
    {
        var rig = new Rig(Snap());

        rig.Feed(Snap(Listening()));

        Assert.Empty(rig.Sink.Shown);
    }

    [Fact]
    public void AProxyThatDiedOnItsOwn_IsReported_Once()
    {
        var rig = new Rig(Snap(Listening()));

        rig.Feed(Snap(Samples.ProxyStopped));
        rig.Feed(Snap(Samples.ProxyStopped));
        rig.Feed(Snap(Samples.ProxyStopped));

        Assert.Equal([Notifier.ProxyDiedText], rig.Bodies);
        Assert.Equal(Notifier.Title, rig.Sink.Shown[0].Title);
    }

    [Fact]
    public void AProxyTheUserStopped_IsNotReported()
    {
        var rig = new Rig(Snap(Listening()));

        rig.Notifier.UserChangedProxy();
        rig.Feed(Snap(Samples.ProxyStopped));

        Assert.Empty(rig.Sink.Shown);
    }

    [Fact]
    public void TheUsersStop_OnlyCoversTheWindowAfterIt()
    {
        var rig = new Rig(Snap(Listening()));
        rig.Notifier.UserChangedProxy();
        rig.Clock.Advance(Notifier.UserWindow + TimeSpan.FromSeconds(1));

        rig.Feed(Snap(Samples.ProxyStopped));

        Assert.Equal([Notifier.ProxyDiedText], rig.Bodies);
    }

    [Fact]
    public void AProxyThatWasRestartedBehindOurBack_IsReported()
    {
        var rig = new Rig(Snap(Listening(pid: 100)));

        rig.Feed(Snap(Listening(pid: 200)));

        Assert.Equal([Notifier.ProxyRestartedText], rig.Bodies);
    }

    [Fact]
    public void ARestartTheUserAskedFor_IsNotReported()
    {
        var rig = new Rig(Snap(Listening(pid: 100)));

        rig.Notifier.UserChangedProxy();
        rig.Feed(Snap(Listening(pid: 200)));

        Assert.Empty(rig.Sink.Shown);
    }

    [Fact]
    public void ASamePidAndAnUnknownPid_AreNotARestart()
    {
        var rig = new Rig(Snap(Listening(pid: 100)));

        rig.Feed(Snap(Listening(pid: 100)));
        rig.Feed(Snap(new ProxyStatus(true, true, 1443, false, Pid: null)));
        rig.Feed(Snap(Listening(pid: 300)));   // from unknown to known: nothing to compare

        Assert.Empty(rig.Sink.Shown);
    }

    // ----- the bypass -----

    [Fact]
    public void TheBypassEnteringError_IsReported_WithWhatTheServiceSaid()
    {
        var rig = new Rig(Snap(bypass: Samples.Bypass("running")));

        rig.Feed(Snap(bypass: Samples.Bypass("error", lastError: "движок не запустился")));
        rig.Feed(Snap(bypass: Samples.Bypass("error", lastError: "движок не запустился")));

        Assert.Equal(["Обход DPI перестал работать: движок не запустился"], rig.Bodies);
    }

    [Fact]
    public void AnErrorWithoutADetail_StillGetsASentence()
    {
        var rig = new Rig(Snap(bypass: Samples.Bypass("running")));

        rig.Feed(Snap(bypass: Samples.Bypass("error")));

        Assert.Equal(["Обход DPI перестал работать."], rig.Bodies);
    }

    [Fact]
    public void TheBypassRecovering_IsReported()
    {
        var rig = new Rig(Snap(bypass: Samples.Bypass("error", lastError: "x")));

        rig.Feed(Snap(bypass: Samples.Bypass("running")));

        Assert.Equal([Notifier.BypassRecoveredText], rig.Bodies);
    }

    [Theory]
    [InlineData("disabled")]
    [InlineData("starting")]
    public void AnErrorThatEndsInAnythingButRunning_IsNotARecovery(string state)
    {
        var rig = new Rig(Snap(bypass: Samples.Bypass("error", lastError: "x")));

        rig.Feed(Snap(bypass: Samples.Bypass(state)));

        Assert.Empty(rig.Sink.Shown);
    }

    [Fact]
    public void AServiceThatIsSimplyGone_IsNotAnError()
    {
        var rig = new Rig(Snap(bypass: Samples.Bypass("running")));

        rig.Feed(Snap(bypass: null));

        Assert.Empty(rig.Sink.Shown);
    }

    // ----- the port -----

    [Fact]
    public void APortSwitch_SaysWhichPortsAndWhatToDo()
    {
        var rig = new Rig();
        var start = new StartResult(StartOutcome.PortSwitched, 1444, OldPort: 1443);

        rig.Feed(Snap(Listening(port: 1444), lastStart: start));
        rig.Feed(Snap(Listening(port: 1444), lastStart: start));   // the same start, seen again

        Assert.Equal(["Порт 1443 занят другим приложением: прокси перешёл на порт 1444. Откройте ссылку в Telegram ещё раз."], rig.Bodies);
    }

    [Fact]
    public void ASecondSwitch_IsReportedAgain_OnceTheThrottleHasPassed()
    {
        var rig = new Rig();
        rig.Feed(Snap(Listening(port: 1444), lastStart: new StartResult(StartOutcome.PortSwitched, 1444, OldPort: 1443)));
        rig.Clock.Advance(Notifier.Throttle + TimeSpan.FromSeconds(1));

        rig.Feed(Snap(Listening(port: 1445), lastStart: new StartResult(StartOutcome.PortSwitched, 1445, OldPort: 1444)));

        Assert.Equal(2, rig.Sink.Shown.Count);
        Assert.Contains("1445", rig.Bodies[1]);
    }

    [Fact]
    public void AnOrdinaryStart_IsNotNews()
    {
        var rig = new Rig();

        rig.Feed(Snap(Listening(), lastStart: new StartResult(StartOutcome.Started, 1443)));

        Assert.Empty(rig.Sink.Shown);
    }

    // ----- the throttle -----

    [Fact]
    public void OneKind_IsSaidAtMostOnceIn30Seconds()
    {
        var rig = new Rig(Snap(Listening()));

        rig.Feed(Snap(Samples.ProxyStopped));   // died
        rig.Clock.Advance(TimeSpan.FromSeconds(10));
        rig.Feed(Snap(Listening()));
        rig.Clock.Advance(TimeSpan.FromSeconds(10));
        rig.Feed(Snap(Samples.ProxyStopped));   // died again, 20 s after the first

        Assert.Single(rig.Sink.Shown);

        rig.Clock.Advance(TimeSpan.FromSeconds(11));
        rig.Feed(Snap(Listening()));
        rig.Clock.Advance(TimeSpan.FromSeconds(1));
        rig.Feed(Snap(Samples.ProxyStopped));   // 32 s after the first

        Assert.Equal(2, rig.Sink.Shown.Count);
    }

    [Fact]
    public void DifferentKinds_AreThrottledSeparately()
    {
        var rig = new Rig(Snap(Listening(), bypass: Samples.Bypass("running")));

        rig.Feed(Snap(Samples.ProxyStopped, bypass: Samples.Bypass("error", lastError: "x")));   // both in one poll

        Assert.Equal(2, rig.Sink.Shown.Count);
    }

    [Fact]
    public void ANoticeThatIsNotATransition_GoesThroughTheSameThrottle()
    {
        var rig = new Rig();

        rig.Notifier.Notify(NoticeKind.TrayHint, Notifier.TrayHintTitle, Notifier.TrayHintText);
        rig.Notifier.Notify(NoticeKind.TrayHint, Notifier.TrayHintTitle, Notifier.TrayHintText);

        Assert.Single(rig.Sink.Shown);
        Assert.Equal(Notifier.TrayHintTitle, rig.Sink.Shown[0].Title);
        Assert.Equal("TgwsOne работает в трее", Notifier.TrayHintTitle);
    }

    [Fact]
    public void AFailedStart_HasItsOwnText()
    {
        Assert.Equal("Не удалось подключиться: порт занят", Notifier.StartFailedText("порт занят"));
    }

    // ----- a sink that fails -----

    [Fact]
    public void ADesktopThatRefuses_NeverReachesTheCaller()
    {
        var rig = new Rig(Snap(Listening()));
        rig.Sink.Throws = new InvalidOperationException("no daemon");

        rig.Feed(Snap(Samples.ProxyStopped));   // no exception, no unobserved fault

        Assert.Single(rig.Sink.Shown);
    }

    // ----- following the real monitor -----

    [Fact]
    public async Task FollowingAMonitor_ReportsWhatItsPollsSee()
    {
        var proxy = new FakeProxyControl { Status = Samples.ProxyListening() };
        await using var monitor = new StatusMonitor(proxy, new FakeControlClient { Unavailable = true }, new FakeNetInfo(), TimeSpan.FromHours(1));
        await monitor.RefreshNowAsync();
        var sink = new RecordingSink();
        new Notifier(sink, new FixedClock(Start)).Attach(monitor);

        proxy.Status = Samples.ProxyStopped;
        await monitor.RefreshNowAsync();

        Assert.Equal([Notifier.ProxyDiedText], sink.Shown.Select(n => n.Body));
    }

    // ----- the user's own actions -----

    [Fact]
    public async Task AStopThroughTheUsersControl_IsNotReported_ButTheSameStopBehindItsBackIs()
    {
        var inner = new FakeProxyControl { Status = Samples.ProxyListening() };
        var rig = new Rig(Snap(Listening()));
        var control = new UserActionProxyControl(inner, rig.Notifier);

        await control.StopAsync(TestContext.Current.CancellationToken);
        rig.Feed(Snap(Samples.ProxyStopped));
        Assert.Empty(rig.Sink.Shown);

        rig.Feed(Snap(Listening()));   // it came back up (started from a terminal, say)
        rig.Clock.Advance(Notifier.UserWindow + TimeSpan.FromSeconds(1));
        rig.Feed(Snap(Samples.ProxyStopped));
        Assert.Equal([Notifier.ProxyDiedText], rig.Bodies);
    }

    [Fact]
    public async Task TheUsersControl_PassesStartStopAndStatusThrough()
    {
        var inner = new FakeProxyControl();
        var control = new UserActionProxyControl(inner, new Notifier(new RecordingSink(), new FixedClock(Start)));

        StartResult started = await control.StartAsync(TestContext.Current.CancellationToken);
        Assert.True(started.Ok);
        Assert.True(control.GetStatus().Listening);
        Assert.Equal(StopOutcome.Stopped, await control.StopAsync(TestContext.Current.CancellationToken));
        Assert.False(control.GetStatus().Running);
        Assert.Equal(1, inner.Starts);
        Assert.Equal(1, inner.Stops);
        inner.RestartIsNeeded = true;
        Assert.True(control.RestartNeeded());
    }

    [Fact]
    public async Task AStartThatThrows_StillMarksTheWindow_AndRethrows()
    {
        var inner = new FakeProxyControl { StartThrows = new IOException("lock timeout") };
        var rig = new Rig(Snap(Listening()));
        var control = new UserActionProxyControl(inner, rig.Notifier);

        await Assert.ThrowsAsync<IOException>(() => control.StartAsync(TestContext.Current.CancellationToken));
        rig.Feed(Snap(Samples.ProxyStopped));

        Assert.Empty(rig.Sink.Shown);   // the user was in the middle of something
    }
}

