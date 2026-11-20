using TgwsOne.Services;

namespace TgwsOne.Tests;

public class CloseBehaviorTests
{
    private sealed class Rig(bool trayAvailable, bool hintShown = false)
    {
        public FakeAppSettings Settings { get; } = new() { TrayHintShown = hintShown };
        public RecordingSink Sink { get; } = new();
        public CloseBehavior Make() => new(trayAvailable, Settings, new Notifier(Sink, new FixedClock(DateTimeOffset.UnixEpoch)));
    }

    [Fact]
    public void WithATray_TheCross_HidesTheWindow()
    {
        var rig = new Rig(trayAvailable: true);

        Assert.Equal(CloseAction.HideToTray, rig.Make().OnClosing(byUser: true));
    }

    [Fact]
    public void WithoutATray_TheCross_MinimizesInstead_AndSaysNothingAboutATray()
    {
        var rig = new Rig(trayAvailable: false);
        CloseBehavior behavior = rig.Make();

        Assert.Equal(CloseAction.Minimize, behavior.OnClosing(byUser: true));
        Assert.Equal(CloseAction.Minimize, behavior.OnClosing(byUser: true));

        Assert.Empty(rig.Sink.Shown);
        Assert.Equal(0, rig.Settings.TrayHintMarks);
    }

    [Fact]
    public void TheFirstHideToTheTray_IsExplained_AndNeverAgain()
    {
        var rig = new Rig(trayAvailable: true);
        CloseBehavior behavior = rig.Make();

        behavior.OnClosing(byUser: true);
        behavior.OnClosing(byUser: true);

        Assert.Equal([(Notifier.TrayHintTitle, Notifier.TrayHintText)], rig.Sink.Shown);
        Assert.Equal(1, rig.Settings.TrayHintMarks);
        Assert.True(rig.Settings.TrayHintShown);
    }

    [Fact]
    public void AHintThatWasShownInAnEarlierRun_IsNotShownAgain()
    {
        var rig = new Rig(trayAvailable: true, hintShown: true);

        Assert.Equal(CloseAction.HideToTray, rig.Make().OnClosing(byUser: true));

        Assert.Empty(rig.Sink.Shown);
        Assert.Equal(0, rig.Settings.TrayHintMarks);
    }

    [Fact]
    public void WhenQuitting_EveryCloseGoesThrough_AndNothingIsSaid()
    {
        var rig = new Rig(trayAvailable: true);
        CloseBehavior behavior = rig.Make();
        behavior.Quitting = true;

        Assert.Equal(CloseAction.Allow, behavior.OnClosing(byUser: true));

        Assert.Empty(rig.Sink.Shown);
    }

    [Fact]
    public void ACloseThatTheUserDidNotMake_GoesThrough()
    {
        // The application shutting itself down, or the session ending: the window must not stand in the way.
        var rig = new Rig(trayAvailable: true);

        Assert.Equal(CloseAction.Allow, rig.Make().OnClosing(byUser: false));
        Assert.Equal(CloseAction.Allow, new Rig(trayAvailable: false).Make().OnClosing(byUser: false));
    }
}
