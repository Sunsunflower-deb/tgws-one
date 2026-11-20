using TgwsOne.Core;
using TgwsOne.Core.Zapret;
using TgwsOne.Services;

namespace TgwsOne.Tests;

public class StatusMonitorTests
{
    private static readonly TimeSpan Hour = TimeSpan.FromHours(1);   // polls only when the test asks for one (plus the first, at start)

    private sealed class Rig : IAsyncDisposable
    {
        public FakeProxyControl Proxy { get; } = new();
        public FakeControlClient Control { get; } = new();
        public FakeNetInfo Net { get; } = new();
        public StatusMonitor Monitor { get; }

        public Rig(TimeSpan? period = null) => Monitor = new StatusMonitor(Proxy, Control, Net, period ?? Hour);

        public ValueTask DisposeAsync() => Monitor.DisposeAsync();
    }

    private static Task<AppSnapshot> WaitFor(StatusMonitor monitor, Func<AppSnapshot, bool> match)
    {
        var tcs = new TaskCompletionSource<AppSnapshot>(TaskCreationOptions.RunContinuationsAsynchronously);
        monitor.Changed += s => { if (match(s)) tcs.TrySetResult(s); };
        if (match(monitor.Current)) tcs.TrySetResult(monitor.Current);   // it may have been published before we subscribed
        return tcs.Task.WaitAsync(TimeSpan.FromSeconds(10));
    }

    [Fact]
    public async Task FirstPoll_PublishesWhatTheFakesSay()
    {
        await using var rig = new Rig();
        rig.Proxy.Status = Samples.ProxyListening(1443);
        rig.Net.Set(Samples.Tcp(1443, 50001), Samples.Tcp(1443, 50002), Samples.Tcp(9999, 50003));
        rig.Control.Status = Samples.Bypass(strategy: "split");

        await rig.Monitor.RefreshNowAsync();

        AppSnapshot s = rig.Monitor.Current;
        Assert.True(s.Proxy.Listening);
        Assert.Equal(2, s.TelegramConnections);
        Assert.True(s.ServiceReachable);
        Assert.Equal("split", s.Bypass!.Strategy);
        Assert.Null(s.LastStart);
        Assert.Equal("status", Assert.Single(rig.Control.Sent.Take(1)).Cmd);
    }

    [Fact]
    public async Task ServiceUnavailable_IsServiceReachableFalse_NotAnError()
    {
        await using var rig = new Rig();
        rig.Control.Unavailable = true;

        await rig.Monitor.RefreshNowAsync();

        Assert.False(rig.Monitor.Current.ServiceReachable);
        Assert.Null(rig.Monitor.Current.Bypass);
    }

    [Fact]
    public async Task AStoppedProxy_HasNoTelegramConnections_EvenIfThePortIsBusyWithOtherThings()
    {
        await using var rig = new Rig();
        rig.Proxy.Status = Samples.ProxyStopped;
        rig.Net.Set(Samples.Tcp(1443, 50001));

        await rig.Monitor.RefreshNowAsync();

        Assert.Equal(0, rig.Monitor.Current.TelegramConnections);
    }

    [Fact]
    public async Task ARefusedStatusRequest_StillCarriesTheStatus()
    {
        await using var rig = new Rig();
        var status = Samples.Bypass("error", lastError: "нет пакета");
        rig.Control.Handler = _ => new ControlResponse(false, "ошибка", Status: status);

        await rig.Monitor.RefreshNowAsync();

        Assert.True(rig.Monitor.Current.ServiceReachable);
        Assert.Equal("error", rig.Monitor.Current.Bypass!.State);
    }

    [Fact]
    public async Task RefreshNow_SeesWhatChangedSinceTheLastPoll()
    {
        await using var rig = new Rig();
        await rig.Monitor.RefreshNowAsync();
        Assert.True(rig.Monitor.Current.ServiceReachable);

        rig.Control.Unavailable = true;
        rig.Proxy.Status = Samples.ProxyListening();
        await rig.Monitor.RefreshNowAsync();

        Assert.False(rig.Monitor.Current.ServiceReachable);
        Assert.True(rig.Monitor.Current.Proxy.Listening);
    }

    [Fact]
    public async Task RecordStart_IsShownAtOnce_AndKeptByLaterPolls()
    {
        await using var rig = new Rig();
        await rig.Monitor.RefreshNowAsync();
        var failed = new StartResult(StartOutcome.Timeout, 1443);

        rig.Monitor.RecordStart(failed);
        Assert.Same(failed, rig.Monitor.Current.LastStart);
        await rig.Monitor.RefreshNowAsync();

        Assert.Same(failed, rig.Monitor.Current.LastStart);
    }

    [Fact]
    public async Task RecordStart_RaisesChanged()
    {
        await using var rig = new Rig();
        await rig.Monitor.RefreshNowAsync();
        var started = new StartResult(StartOutcome.Started, 1443);

        Task<AppSnapshot> seen = WaitFor(rig.Monitor, s => s.LastStart == started);
        rig.Monitor.RecordStart(started);

        Assert.Same(started, (await seen).LastStart);
    }

    [Fact]
    public async Task Polls_RepeatOnTheirOwn()
    {
        await using var rig = new Rig(TimeSpan.FromMilliseconds(30));

        await WaitFor(rig.Monitor, _ => rig.Control.Sent.Count >= 3);

        Assert.True(rig.Control.Sent.Count >= 3);
    }

    private sealed class RecordingContext : SynchronizationContext
    {
        private int _posts;
        public int Posts => Volatile.Read(ref _posts);

        public override void Post(SendOrPostCallback d, object? state)
        {
            Interlocked.Increment(ref _posts);
            d(state);
        }
    }

    [Fact]
    public async Task Changed_IsDeliveredThroughTheContextTheMonitorWasCreatedOn()
    {
        var context = new RecordingContext();
        SynchronizationContext? before = SynchronizationContext.Current;
        SynchronizationContext.SetSynchronizationContext(context);   // as if created on the UI thread
        StatusMonitor monitor;
        try { monitor = new StatusMonitor(new FakeProxyControl(), new FakeControlClient(), new FakeNetInfo(), Hour); }
        finally { SynchronizationContext.SetSynchronizationContext(before); }

        await using (monitor)
        {
            int raised = 0;
            monitor.Changed += _ => Interlocked.Increment(ref raised);
            await monitor.RefreshNowAsync();

            Assert.True(context.Posts >= 1);
            Assert.True(Volatile.Read(ref raised) >= 1);
        }
    }

    [Fact]
    public async Task AFaultySubscriber_DoesNotStopTheMonitoring()
    {
        await using var rig = new Rig(TimeSpan.FromMilliseconds(30));
        rig.Monitor.Changed += _ => throw new InvalidOperationException("subscriber bug");

        await WaitFor(rig.Monitor, _ => rig.Control.Sent.Count >= 3);

        Assert.True(rig.Control.Sent.Count >= 3);
    }

    [Fact]
    public async Task AFailingPoll_DoesNotEndTheMonitoring()
    {
        await using var rig = new Rig(TimeSpan.FromMilliseconds(30));
        int calls = 0;
        rig.Proxy.OnGetStatus = () => Interlocked.Increment(ref calls) <= 2 ? throw new IOException("pid file locked") : Samples.ProxyListening();

        AppSnapshot s = await WaitFor(rig.Monitor, s => s.Proxy.Listening);

        Assert.True(s.Proxy.Listening);
        Assert.True(Volatile.Read(ref calls) >= 3);
    }

    [Fact]
    public async Task Dispose_StopsPolling()
    {
        var rig = new Rig(TimeSpan.FromMilliseconds(20));
        await WaitFor(rig.Monitor, _ => rig.Control.Sent.Count >= 2);

        await rig.DisposeAsync();
        int after = rig.Control.Sent.Count;
        await Task.Delay(200, TestContext.Current.CancellationToken);

        Assert.Equal(after, rig.Control.Sent.Count);
    }

    [Fact]
    public async Task RefreshNow_AfterDispose_DoesNotThrow()
    {
        var rig = new Rig();
        await rig.DisposeAsync();

        await rig.Monitor.RefreshNowAsync();
    }

    /// <summary>A UI-thread stand-in: what is posted to it only runs when somebody pumps it, and nobody does while the thread is blocked.</summary>
    private sealed class UnpumpedContext : SynchronizationContext
    {
        public override void Post(SendOrPostCallback d, object? state)
        {
            // queued for a thread that is busy waiting for us: it never runs
        }
    }

    [Fact]
    public void Disposing_FromABlockedUiThread_DoesNotWaitForThatThread()
    {
        // The application's Exit handler blocks the UI thread until the monitor is disposed. If disposal needed that very thread to
        // finish (an await that resumes on the captured context), quitting would hang for good with the window gone.
        SynchronizationContext? before = SynchronizationContext.Current;
        SynchronizationContext.SetSynchronizationContext(new UnpumpedContext());
        try
        {
            var monitor = new StatusMonitor(new FakeProxyControl(), new FakeControlClient(), new FakeNetInfo(), Hour);

            Task disposal = monitor.DisposeAsync().AsTask();

#pragma warning disable xUnit1031   // blocking this thread is the point: it is what the Exit handler does
            Assert.True(disposal.Wait(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken), "disposal is waiting for the thread that is waiting for it");
#pragma warning restore xUnit1031
        }
        finally
        {
            SynchronizationContext.SetSynchronizationContext(before);
        }
    }
}

