using TgwsOne.Engine.Desync;

namespace TgwsOne.Engine.Tests.Desync;

public sealed class DesyncControllerTests
{
    [Fact]
    public void SelectingAStrategy_ChangesTheAnswer_AndRaisesChangedOnce()
    {
        var controller = new DesyncController();
        int changes = 0;
        controller.Changed += () => changes++;

        var split = new DesyncStrategy("split", [new Split(new SniMid())]);
        var disorder = new DesyncStrategy("disorder", [new Disorder(new Absolute(1))]);

        controller.Select(split);
        Assert.Same(split, controller.Resolve("example.test", "ws"));
        Assert.False(controller.IsAuto);

        controller.Select(disorder);
        Assert.Same(disorder, controller.Resolve("example.test", "ws"));
        Assert.Equal(2, changes);

        // Selecting the same strategy again is not a change.
        controller.Select(disorder);
        Assert.Equal(2, changes);
    }

    [Fact]
    public void UseAuto_ReturnsToTheRotation_AndAnnouncesTheChange()
    {
        var first = new DesyncStrategy("first", [new Split(new SniMid())]);
        var second = new DesyncStrategy("second", [new Split(new SniEnd(1))]);
        var controller = new DesyncController([first, second]);
        int changes = 0;
        controller.Changed += () => changes++;

        controller.Select(first);
        controller.UseAuto();

        Assert.True(controller.IsAuto);
        Assert.Same(first, controller.Resolve("example.test", "ws"));
        Assert.Equal(2, changes);
    }

    [Fact]
    public void AutoStartsEveryHostAtTheFirstStrategy()
    {
        var first = new DesyncStrategy("first", [new Split(new SniMid())]);
        var second = new DesyncStrategy("second", [new Split(new SniEnd(1))]);
        var controller = new DesyncController([first, second]);

        Assert.Same(first, controller.Resolve("a.example", "ws"));
        Assert.Same(first, controller.Resolve("b.example", "ws"));
        Assert.Same(first, controller.Resolve("a.example", "h2"));
    }

    [Fact]
    public void NoRotation_ResolvesToNoDesync()
    {
        var controller = new DesyncController([]);

        Assert.Null(controller.Resolve("example.test", "ws"));
        Assert.Empty(controller.Rotation);
    }

    [Fact]
    public void SetRotation_ReplacesTheSetAndStartsTheHostsOver()
    {
        var first = new DesyncStrategy("first", [new Split(new SniMid())]);
        var second = new DesyncStrategy("second", [new Split(new SniEnd(1))]);
        var controller = new DesyncController([first]);
        int changes = 0;
        controller.Changed += () => changes++;

        controller.SetRotation([second]);

        Assert.Equal([second], controller.Rotation);
        Assert.Same(second, controller.Resolve("example.test", "ws"));
        Assert.Equal(1, changes);
    }
}
