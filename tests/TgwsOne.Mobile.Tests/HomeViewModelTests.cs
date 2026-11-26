using TgwsOne.Engine;

namespace TgwsOne.Mobile.Tests;

public class HomeViewModelTests
{
    private sealed class Rig
    {
        public FakeEngineControl Engine { get; } = new()
        {
            Status = new EngineProxyStatus(false, false, 0, PortBusyByOther: false, Pid: null),
        };
        public FakeMobileSettings Settings { get; } = new();
        public long Connections { get; set; }
        public HomeViewModel Vm { get; }

        public Rig()
        {
            Vm = new HomeViewModel(Engine, Settings, () => Connections);
        }
    }

    [Fact]
    public void Initially_ItIsStopped_AndOneButtonWouldStartIt()
    {
        var rig = new Rig();

        Assert.False(rig.Vm.IsRunning);
        Assert.Equal("Запустить", rig.Vm.ButtonText);
        Assert.Equal(("Отключено", Tone.Grey), (rig.Vm.Headline, rig.Vm.HeadlineTone));
        Assert.Equal(("остановлен", Tone.Grey), (rig.Vm.ProxyLine, rig.Vm.ProxyTone));
    }

    [Fact]
    public async Task TheOneButton_StartsTheEngine_AndSaysTheLinkIsNext()
    {
        var rig = new Rig();

        await rig.Vm.ToggleCommand.ExecuteAsync(null);

        Assert.Equal(1, rig.Engine.Starts);
        Assert.True(rig.Vm.IsRunning);
        Assert.Equal("Остановить", rig.Vm.ButtonText);
        Assert.Equal(("Прокси готов — откройте ссылку в Telegram", Tone.Amber), (rig.Vm.Headline, rig.Vm.HeadlineTone));
        Assert.Equal(("работает · 127.0.0.1:1443", Tone.Green), (rig.Vm.ProxyLine, rig.Vm.ProxyTone));
        Assert.Null(rig.Vm.Banner);
    }

    [Fact]
    public async Task WithTelegramAttached_ThePhraseIsGreen()
    {
        var rig = new Rig { Connections = 2 };

        await rig.Vm.ToggleCommand.ExecuteAsync(null);

        Assert.Equal(("Telegram работает через прокси", Tone.Green), (rig.Vm.Headline, rig.Vm.HeadlineTone));
    }

    [Fact]
    public async Task TheSameButton_StopsTheEngine()
    {
        var rig = new Rig();
        await rig.Vm.ToggleCommand.ExecuteAsync(null);

        await rig.Vm.ToggleCommand.ExecuteAsync(null);

        Assert.Equal(1, rig.Engine.Stops);
        Assert.False(rig.Vm.IsRunning);
        Assert.Equal("Запустить", rig.Vm.ButtonText);
        Assert.Equal("Отключено", rig.Vm.Headline);
    }

    [Fact]
    public async Task AFailedStart_IsThePhrase_AndAnExplanation()
    {
        var rig = new Rig();
        rig.Engine.NextStart = new EngineStartResult(EngineStartOutcome.EngineExited, 1443, ExitCode: 3);

        await rig.Vm.ToggleCommand.ExecuteAsync(null);

        Assert.False(rig.Vm.IsRunning);
        Assert.Equal(("Прокси не запустился: движок завершился с кодом 3", Tone.Red),
                     (rig.Vm.Headline, rig.Vm.HeadlineTone));
        Assert.Contains("Не удалось запустить прокси: движок завершился с кодом 3", rig.Vm.Banner!.Text);
        Assert.Equal(Tone.Red, rig.Vm.Banner.Tone);
    }

    [Fact]
    public async Task APortSwitch_AsksToOpenTheLinkAgain()
    {
        var rig = new Rig();
        rig.Engine.NextStart = new EngineStartResult(EngineStartOutcome.PortSwitched, 1444, OldPort: 1443);

        await rig.Vm.ToggleCommand.ExecuteAsync(null);

        Assert.Equal(new HomeBanner("Порт 1443 занят — прокси на 1444. Откройте ссылку в Telegram заново.", Tone.Amber),
                     rig.Vm.Banner);
    }

    [Fact]
    public async Task AStartThatThrows_LeavesAnExplanation()
    {
        var rig = new Rig();
        rig.Engine.StartThrows = new TimeoutException("запуск не завершился");

        await rig.Vm.ToggleCommand.ExecuteAsync(null);

        Assert.Equal("Не удалось запустить прокси: запуск не завершился", rig.Vm.Banner!.Text);
        Assert.False(rig.Vm.IsRunning);
    }
}
