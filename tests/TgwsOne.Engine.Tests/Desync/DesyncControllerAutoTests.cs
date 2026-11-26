using TgwsOne.Engine.Desync;

namespace TgwsOne.Engine.Tests.Desync;

public sealed class DesyncControllerAutoTests
{
    private static readonly DesyncStrategy First = new("first", [new Split(new SniMid())]);
    private static readonly DesyncStrategy Second = new("second", [new Split(new SniEnd(1))]);
    private static readonly DesyncStrategy Third = new("third", [new Split(new Absolute(1))]);

    private static DesyncController Controller(FakeTime time)
        => new([First, Second, Third], time);

    private static void Fail(DesyncController controller, string host, string strategy, int times = 1)
    {
        for (int i = 0; i < times; i++)
            controller.Report(host, "ws", strategy, ok: false, EgressFailure.TlsReset, TimeSpan.FromMilliseconds(1));
    }

    [Fact]
    public void ThreeFailuresWithinTheWindow_MoveToTheNextStrategy()
    {
        var time = new FakeTime();
        DesyncController controller = Controller(time);
        Assert.Same(First, controller.Resolve("host.example", "ws"));

        Fail(controller, "host.example", First.Id, 2);
        Assert.Same(First, controller.Resolve("host.example", "ws"));
        Assert.Equal(new AutoHost("host.example", First.Id, 2), Assert.Single(controller.AutoHosts));

        Fail(controller, "host.example", First.Id);
        Assert.Same(Second, controller.Resolve("host.example", "ws"));
        Assert.Equal(new AutoHost("host.example", Second.Id, 0), Assert.Single(controller.AutoHosts));
    }

    [Fact]
    public void SuccessResetsTheCounter()
    {
        var time = new FakeTime();
        DesyncController controller = Controller(time);
        controller.Resolve("host.example", "ws");
        Fail(controller, "host.example", First.Id, 2);

        controller.Report("host.example", "ws", First.Id, ok: true, EgressFailure.None, TimeSpan.Zero);
        Assert.Equal(new AutoHost("host.example", First.Id, 0), Assert.Single(controller.AutoHosts));

        Fail(controller, "host.example", First.Id, 2);
        Assert.Same(First, controller.Resolve("host.example", "ws"));
    }

    [Fact]
    public void TheRotationWrapsAround()
    {
        var time = new FakeTime();
        DesyncController controller = Controller(time);
        controller.Resolve("host.example", "ws");

        Fail(controller, "host.example", First.Id, 3);
        Assert.Same(Second, controller.Resolve("host.example", "ws"));
        Fail(controller, "host.example", Second.Id, 3);
        Assert.Same(Third, controller.Resolve("host.example", "ws"));
        Fail(controller, "host.example", Third.Id, 3);
        Assert.Same(First, controller.Resolve("host.example", "ws"));
    }

    [Fact]
    public void FailuresOlderThanTheWindow_DoNotCount()
    {
        var time = new FakeTime();
        DesyncController controller = Controller(time);
        controller.Resolve("host.example", "ws");

        Fail(controller, "host.example", First.Id);
        time.Advance(TimeSpan.FromSeconds(30));
        Fail(controller, "host.example", First.Id);
        time.Advance(TimeSpan.FromSeconds(31));   // the first failure is now 61 s old
        Fail(controller, "host.example", First.Id);
        Assert.Same(First, controller.Resolve("host.example", "ws"));
        Assert.Equal(new AutoHost("host.example", First.Id, 2), Assert.Single(controller.AutoHosts));

        time.Advance(TimeSpan.FromSeconds(1));
        Fail(controller, "host.example", First.Id);
        Assert.Same(Second, controller.Resolve("host.example", "ws"));
    }

    [Fact]
    public void HostsMoveIndependently()
    {
        var time = new FakeTime();
        DesyncController controller = Controller(time);
        controller.Resolve("a.example", "ws");
        controller.Resolve("b.example", "ws");

        Fail(controller, "a.example", First.Id, 3);

        Assert.Same(Second, controller.Resolve("a.example", "ws"));
        Assert.Same(First, controller.Resolve("b.example", "ws"));
    }

    [Fact]
    public void ALateReportForThePreviousStrategy_IsIgnored()
    {
        var time = new FakeTime();
        DesyncController controller = Controller(time);
        controller.Resolve("host.example", "ws");

        Fail(controller, "host.example", Second.Id, 3);

        Assert.Same(First, controller.Resolve("host.example", "ws"));
    }

    [Fact]
    public void AnExplicitSelection_IsNeverRotated()
    {
        var time = new FakeTime();
        DesyncController controller = Controller(time);
        controller.Select(Second);

        Fail(controller, "host.example", Second.Id, 5);

        Assert.Same(Second, controller.Resolve("host.example", "ws"));
        Assert.Empty(controller.AutoHosts);
    }

    [Fact]
    public void TheRotationChange_RaisesChanged()
    {
        var time = new FakeTime();
        DesyncController controller = Controller(time);
        controller.Resolve("host.example", "ws");
        int changes = 0;
        controller.Changed += () => changes++;

        Fail(controller, "host.example", First.Id, 3);

        Assert.Equal(1, changes);
    }

    private sealed class FakeTime : TimeProvider
    {
        private DateTimeOffset _now = new(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);

        public override DateTimeOffset GetUtcNow() => _now;

        public void Advance(TimeSpan delta) => _now += delta;
    }
}
