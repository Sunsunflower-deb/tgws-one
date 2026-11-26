using TgwsOne.Engine.Desync;

namespace TgwsOne.Mobile.Tests;

public class BypassViewModelTests
{
    private sealed class Rig
    {
        public FakeBypassControl Control { get; } = new();
        public FakeMobileSettings Settings { get; } = new();
        public BypassViewModel Vm { get; }

        public Rig()
        {
            Vm = new BypassViewModel(Control, Settings);
        }

        public async Task LoadAsync() => await Vm.LoadAsync();
    }

    [Fact]
    public async Task Auto_ShowsTheSwitchOn_AndWhatItChosePerHost()
    {
        var rig = new Rig();
        rig.Control.AutoHosts =
        [
            new AutoHost("kws2.web.telegram.org", "split-sni", 2),
            new AutoHost("kws4.web.telegram.org", "fake", 0),
        ];

        await rig.LoadAsync();

        Assert.True(rig.Vm.IsBypassOn);
        Assert.Equal("Выключить обход", rig.Vm.ButtonText);
        Assert.Equal(("включён · Авто · kws2 → Разрез после имени, ещё 1", Tone.Green),
                     (rig.Vm.StatusLine, rig.Vm.StatusTone));
        Assert.True(rig.Vm.ShowAuto);
        Assert.False(rig.Vm.AutoIsEmpty);
        Assert.Equal(["kws2.web.telegram.org", "kws4.web.telegram.org"], rig.Vm.AutoRows.Select(row => row.Host));
        Assert.Equal(["Разрез после имени", "Фейковый ClientHello"], rig.Vm.AutoRows.Select(row => row.StrategyName));
        Assert.Equal(["2 сбоя", "—"], rig.Vm.AutoRows.Select(row => row.Failures));
        Assert.True(rig.Vm.Strategies.Single(option => option.Id == DesyncFinder.AutoId).IsSelected);
    }

    [Fact]
    public async Task ThePicker_OffersAutoAndEveryBuiltInStrategy()
    {
        var rig = new Rig();

        await rig.LoadAsync();

        Assert.Equal([DesyncFinder.AutoId, .. BuiltInDesync.Ids], rig.Vm.Strategies.Select(option => option.Id));
        Assert.Equal("Авто", rig.Vm.Strategies[0].Name);
        Assert.All(rig.Vm.Strategies.Skip(1), option => Assert.Equal(BuiltInDesync.Find(option.Id)!.Title, option.Name));
    }

    [Fact]
    public async Task ADisabledBypass_SaysSo_AndTheButtonWouldTurnItOn()
    {
        var rig = new Rig();
        rig.Control.Enabled = false;
        rig.Control.Strategy = DesyncFinder.NoBypassId;

        await rig.LoadAsync();

        Assert.False(rig.Vm.IsBypassOn);
        Assert.Equal("Включить обход", rig.Vm.ButtonText);
        Assert.Equal(("выключен", Tone.Grey), (rig.Vm.StatusLine, rig.Vm.StatusTone));
        Assert.DoesNotContain(rig.Vm.Strategies, option => option.IsSelected);
    }

    [Fact]
    public async Task TurningItOn_UsesTheSavedStrategy()
    {
        var rig = new Rig();
        rig.Settings.Android = new AndroidSettings("split-sni", "mts", 60);
        rig.Control.Enabled = false;
        rig.Control.Strategy = DesyncFinder.NoBypassId;
        await rig.LoadAsync();

        await rig.Vm.ToggleCommand.ExecuteAsync(null);

        Assert.Equal(["split-sni"], rig.Control.Applied);
        Assert.True(rig.Vm.IsBypassOn);
        Assert.Equal("включён · Разрез после имени", rig.Vm.StatusLine);
        Assert.True(rig.Vm.Strategies.Single(option => option.Id == "split-sni").IsSelected);
    }

    [Fact]
    public async Task TurningItOff_ChoosesNoBypass()
    {
        var rig = new Rig();
        await rig.LoadAsync();

        await rig.Vm.ToggleCommand.ExecuteAsync(null);

        Assert.Equal([DesyncFinder.NoBypassId], rig.Control.Applied);
        Assert.False(rig.Vm.IsBypassOn);
        Assert.Equal("выключен", rig.Vm.StatusLine);
    }

    [Fact]
    public async Task ChoosingAStrategy_AppliesIt_AndRemembersItForTheNextStart()
    {
        var rig = new Rig();
        await rig.LoadAsync();

        StrategyOption split = rig.Vm.Strategies.Single(option => option.Id == "split-sni");
        await split.ChooseCommand.ExecuteAsync(null);

        Assert.Equal(["split-sni"], rig.Control.Applied);
        Assert.Equal("split-sni", rig.Settings.Android.Strategy);
        Assert.Equal(1, rig.Settings.Saves);
        Assert.Equal("split-sni", rig.Vm.CurrentStrategy);
        Assert.True(split.IsSelected);
        Assert.False(rig.Vm.ShowAuto);
    }

    [Fact]
    public async Task ChoosingAuto_SwitchesTheControllerBackToAuto()
    {
        var rig = new Rig();
        rig.Control.Strategy = "split-sni";
        await rig.LoadAsync();

        StrategyOption auto = rig.Vm.Strategies.Single(option => option.Id == DesyncFinder.AutoId);
        await auto.ChooseCommand.ExecuteAsync(null);

        Assert.Equal([DesyncFinder.AutoId], rig.Control.Applied);
        Assert.Equal(DesyncFinder.AutoId, rig.Settings.Android.Strategy);
        Assert.True(rig.Vm.ShowAuto);
        Assert.True(auto.IsSelected);
    }

    [Fact]
    public async Task ARefusedChoice_LeavesTheOldSelection_AndSaysWhy()
    {
        var rig = new Rig();
        rig.Control.Refuse = id => id == "split-sni" ? "стратегия не прошла проверку" : null;
        await rig.LoadAsync();

        StrategyOption split = rig.Vm.Strategies.Single(option => option.Id == "split-sni");
        await split.ChooseCommand.ExecuteAsync(null);

        Assert.Equal("стратегия не прошла проверку", rig.Vm.ErrorText);
        Assert.Equal(DesyncFinder.AutoId, rig.Vm.CurrentStrategy);
        Assert.True(rig.Vm.Strategies.Single(option => option.Id == DesyncFinder.AutoId).IsSelected);
        Assert.Equal(0, rig.Settings.Saves);
    }

    [Fact]
    public async Task AutoWithoutAnyHostYet_ExplainsThatItNeedsTelegramTraffic()
    {
        var rig = new Rig();

        await rig.LoadAsync();

        Assert.True(rig.Vm.ShowAuto);
        Assert.True(rig.Vm.AutoIsEmpty);
        Assert.Equal("включён · Авто", rig.Vm.StatusLine);
        Assert.Empty(rig.Vm.AutoRows);
    }
}
