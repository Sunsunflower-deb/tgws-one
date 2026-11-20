using TgwsOne.Services;
using TgwsOne.ViewModels;

namespace TgwsOne.Tests;

/// <summary>The first-run wizard's view-model over fakes: the real <see cref="StatusMonitor"/> and <see cref="Diagnostics"/>, scripted connect and Telegram actions.</summary>
internal sealed class WizardRig : IAsyncDisposable
{
    /// <summary>What the page buttons do: connecting starts the (fake) proxy unless <see cref="ConnectProblem"/> is set.</summary>
    private sealed class Actions(WizardRig rig) : IDiagnosticsActions
    {
        public async Task<string?> ConnectAsync()
        {
            rig.ConnectCalls++;
            if (rig.ConnectGate is { } gate) await gate;
            if (rig.ConnectProblem is { } problem) return problem;
            await rig.Proxy.StartAsync();
            return null;
        }

        public Task<string?> OpenInTelegramAsync()
        {
            rig.TelegramCalls++;
            return Task.FromResult(rig.TelegramNotice);
        }

        public Task<string?> EnableBypassAsync() => Task.FromResult<string?>(null);
        public Task<string?> FindStrategyAsync() => Task.FromResult<string?>(null);
    }

    public FakeProxyControl Proxy { get; } = new();
    public FakeBypassService Service { get; } = new();
    public FakeNetInfo Net { get; } = new();
    public FakeAppSettings Settings { get; } = new();
    public FakeAutostart Autostart { get; } = new();
    public FixedProbes Probes { get; } = new();
    public StatusMonitor Monitor { get; }
    public Diagnostics Diagnostics { get; }
    public WizardViewModel Vm { get; }

    public string? ConnectProblem { get; set; }
    public string? TelegramNotice { get; set; }
    public Task? ConnectGate { get; set; }
    public int ConnectCalls { get; private set; }
    public int TelegramCalls { get; private set; }

    public WizardRig()
    {
        Monitor = new StatusMonitor(Proxy, Service, Net, TimeSpan.FromHours(1));
        Diagnostics = new Diagnostics(Proxy, Service, Net, Settings, Probes, Probes, Probes, () => true);
        Vm = new WizardViewModel(new Actions(this), Monitor, Diagnostics, Settings, Autostart);
    }

    /// <summary>Telegram connects to the proxy, as the connection table shows it.</summary>
    public async Task TelegramConnectsAsync()
    {
        Net.Set(Samples.Tcp(1443, 50001));
        await Monitor.RefreshNowAsync();
    }

    public async Task ConnectAndContinueAsync()
    {
        await Vm.StartCommand.ExecuteAsync(null);
        Vm.NextCommand.Execute(null);
    }

    public ValueTask DisposeAsync() => Monitor.DisposeAsync();
}

public class WizardViewModelTests
{
    // ----- step 1: the connection -----

    [Fact]
    public async Task ItStartsOnTheConnectionStep_WithNothingTried()
    {
        await using var rig = new WizardRig();

        Assert.True(rig.Vm.IsConnectStep);
        Assert.False(rig.Vm.IsTelegramStep);
        Assert.False(rig.Vm.IsDoneStep);
        Assert.Equal("Шаг 1 из 3", rig.Vm.StepText);
        Assert.False(rig.Vm.ConnectFinished);
        Assert.False(rig.Vm.ConnectFailed);
        Assert.Equal("остановлен", rig.Vm.ProxyLine);
        Assert.Equal(Tone.Grey, rig.Vm.ProxyTone);
        Assert.False(rig.Vm.NextCommand.CanExecute(null));   // the connection has to be tried first
        Assert.True(rig.Vm.ShowNext);
        Assert.False(rig.Vm.ShowSkip);
        Assert.Equal(0, rig.Settings.FirstRunMarks);
    }

    [Fact]
    public async Task Connecting_ShowsTheProxyAndBypassLines_AndAllowsMovingOn()
    {
        await using var rig = new WizardRig();

        await rig.Vm.StartCommand.ExecuteAsync(null);

        Assert.True(rig.Vm.Connected);
        Assert.True(rig.Vm.ConnectFinished);
        Assert.False(rig.Vm.ConnectFailed);
        Assert.Null(rig.Vm.Problem);
        Assert.Equal("работает · 127.0.0.1:1443", rig.Vm.ProxyLine);
        Assert.Equal(Tone.Green, rig.Vm.ProxyTone);
        Assert.Equal(StatusText.BypassLine(rig.Monitor.Current).Text, rig.Vm.BypassLine);
        Assert.True(rig.Vm.NextCommand.CanExecute(null));
        Assert.Equal(1, rig.ConnectCalls);
    }

    [Fact]
    public async Task WhileConnecting_NeitherStartNorNextIsAvailable()
    {
        await using var rig = new WizardRig();
        var gate = new TaskCompletionSource();
        rig.ConnectGate = gate.Task;

        Task run = rig.Vm.StartCommand.ExecuteAsync(null);

        Assert.True(rig.Vm.IsConnecting);
        Assert.False(rig.Vm.StartCommand.CanExecute(null));
        Assert.False(rig.Vm.NextCommand.CanExecute(null));

        gate.SetResult();
        await run;

        Assert.False(rig.Vm.IsConnecting);
        Assert.True(rig.Vm.NextCommand.CanExecute(null));
    }

    [Fact]
    public async Task AFailedConnection_ShowsWhatHappened_AndTheDiagnosticsVerdict()
    {
        await using var rig = new WizardRig();
        rig.ConnectProblem = "Порт 1443 занят другим приложением.";

        await rig.Vm.StartCommand.ExecuteAsync(null);

        Assert.False(rig.Vm.Connected);
        Assert.True(rig.Vm.ConnectFailed);
        Assert.Equal($"Порт 1443 занят другим приложением.\n{rig.Diagnostics.Decide().Text}", rig.Vm.Problem);
        Assert.Equal("Прокси выключен.", rig.Diagnostics.Decide().Text);
        Assert.True(rig.Vm.NextCommand.CanExecute(null));   // it can be carried on: Главная has all of it
    }

    [Fact]
    public async Task AConnectionThatLeavesTheProxyDown_IsAFailure_EvenWithoutAMessage()
    {
        await using var rig = new WizardRig();
        rig.Proxy.OnGetStatus = () => Samples.ProxyStopped;   // «Подключить» reported nothing, yet nothing listens

        await rig.Vm.StartCommand.ExecuteAsync(null);

        Assert.True(rig.Vm.ConnectFailed);
        Assert.Equal("Прокси выключен.", rig.Vm.Problem);
    }

    [Fact]
    public async Task TryingAgain_ClearsTheProblem_WhenItWorksThisTime()
    {
        await using var rig = new WizardRig();
        rig.ConnectProblem = "не вышло";
        await rig.Vm.StartCommand.ExecuteAsync(null);
        Assert.True(rig.Vm.ConnectFailed);

        rig.ConnectProblem = null;
        await rig.Vm.StartCommand.ExecuteAsync(null);

        Assert.False(rig.Vm.ConnectFailed);
        Assert.Null(rig.Vm.Problem);
        Assert.True(rig.Vm.Connected);
        Assert.Equal(2, rig.ConnectCalls);
    }

    // ----- step 2: Telegram -----

    [Fact]
    public async Task Next_MovesThroughTheThreeSteps_AndStopsAtTheLast()
    {
        await using var rig = new WizardRig();
        await rig.Vm.StartCommand.ExecuteAsync(null);

        rig.Vm.NextCommand.Execute(null);
        Assert.True(rig.Vm.IsTelegramStep);
        Assert.Equal("Шаг 2 из 3", rig.Vm.StepText);

        rig.Vm.NextCommand.Execute(null);
        Assert.True(rig.Vm.IsDoneStep);
        Assert.Equal("Шаг 3 из 3", rig.Vm.StepText);

        Assert.False(rig.Vm.NextCommand.CanExecute(null));
        rig.Vm.NextCommand.Execute(null);
        Assert.True(rig.Vm.IsDoneStep);
    }

    [Fact]
    public void TheHint_IsTheOneFromTheSpec()
    {
        Assert.Equal("Telegram покажет окно «Proxy Server» / «Прокси-сервер» — нажмите «Connect Proxy» / «Подключить прокси»", WizardViewModel.TelegramHint);
    }

    [Fact]
    public async Task OpeningTelegram_AsksTheHomePageToDoIt_AndShowsWhatItSaid()
    {
        await using var rig = new WizardRig();
        rig.TelegramNotice = "Telegram не открылся: ссылка скопирована.";
        await rig.ConnectAndContinueAsync();

        await rig.Vm.OpenTelegramCommand.ExecuteAsync(null);

        Assert.Equal(1, rig.TelegramCalls);
        Assert.Equal("Telegram не открылся: ссылка скопирована.", rig.Vm.Notice);
    }

    [Fact]
    public async Task WithoutATelegramConnection_ThereIsSkip_NotNext()
    {
        await using var rig = new WizardRig();
        await rig.ConnectAndContinueAsync();

        Assert.False(rig.Vm.TelegramConnected);
        Assert.True(rig.Vm.ShowSkip);
        Assert.False(rig.Vm.ShowNext);
    }

    [Fact]
    public async Task OnceTelegramConnects_TheStepIsDone_AndNextReplacesSkip()
    {
        await using var rig = new WizardRig();
        await rig.ConnectAndContinueAsync();
        var changed = new List<string?>();
        rig.Vm.PropertyChanged += (_, e) => changed.Add(e.PropertyName);

        await rig.TelegramConnectsAsync();

        Assert.True(rig.Vm.TelegramConnected);
        Assert.False(rig.Vm.ShowSkip);
        Assert.True(rig.Vm.ShowNext);
        Assert.Contains(nameof(WizardViewModel.ShowNext), changed);
        Assert.Contains(nameof(WizardViewModel.ShowSkip), changed);
    }

    [Fact]
    public async Task Skip_GoesToTheLastStep_WithoutTelegram()
    {
        await using var rig = new WizardRig();
        await rig.ConnectAndContinueAsync();

        rig.Vm.NextCommand.Execute(null);   // «Пропустить» is the same step forward

        Assert.True(rig.Vm.IsDoneStep);
        Assert.False(rig.Vm.TelegramConnected);
    }

    [Fact]
    public async Task MovingOn_DropsTheNoticeOfTheStepBefore()
    {
        await using var rig = new WizardRig();
        rig.TelegramNotice = "ссылка скопирована";
        await rig.ConnectAndContinueAsync();
        await rig.Vm.OpenTelegramCommand.ExecuteAsync(null);

        rig.Vm.NextCommand.Execute(null);

        Assert.Null(rig.Vm.Notice);
    }

    // ----- step 3 and the end -----

    [Fact]
    public async Task Autostart_IsOnByDefault()
    {
        await using var rig = new WizardRig();

        Assert.True(rig.Vm.AutostartOn);
    }

    [Fact]
    public async Task Finish_WritesTheKey_TurnsAutostartOn_AndClosesTheWindow()
    {
        await using var rig = new WizardRig();
        await rig.ConnectAndContinueAsync();
        rig.Vm.NextCommand.Execute(null);
        int closed = 0;
        rig.Vm.CloseRequested += () => closed++;

        rig.Vm.FinishCommand.Execute(null);

        Assert.True(rig.Settings.FirstRunDone);
        Assert.Equal(1, rig.Settings.FirstRunMarks);
        Assert.Equal([true], rig.Autostart.Calls);
        Assert.Equal(1, closed);
    }

    [Fact]
    public async Task Finish_WithTheBoxCleared_TurnsAutostartOff()
    {
        await using var rig = new WizardRig();
        rig.Autostart.Enabled = true;
        await rig.ConnectAndContinueAsync();
        rig.Vm.NextCommand.Execute(null);
        rig.Vm.AutostartOn = false;

        rig.Vm.FinishCommand.Execute(null);

        Assert.Equal([false], rig.Autostart.Calls);
        Assert.False(rig.Autostart.Enabled);
        Assert.True(rig.Settings.FirstRunDone);
    }

    [Fact]
    public async Task AnAutostartThatCannotBeWritten_IsSaidOnce_ThenTheSecondPressEndsTheWizard()
    {
        await using var rig = new WizardRig();
        rig.Autostart.Throws = new UnauthorizedAccessException("профиль только для чтения");
        await rig.ConnectAndContinueAsync();
        rig.Vm.NextCommand.Execute(null);
        int closed = 0;
        rig.Vm.CloseRequested += () => closed++;

        rig.Vm.FinishCommand.Execute(null);

        Assert.Equal(0, closed);
        Assert.Contains("профиль только для чтения", rig.Vm.Notice);
        Assert.Contains("в настройках", rig.Vm.Notice);
        Assert.True(rig.Settings.FirstRunDone);   // it is not shown again either way

        rig.Vm.FinishCommand.Execute(null);

        Assert.Equal(1, closed);
        Assert.Equal([true], rig.Autostart.Calls);   // not retried
        Assert.Equal(1, rig.Settings.FirstRunMarks);
    }

    [Fact]
    public async Task ClosingTheWindowSomeOtherWay_StillMeansItWasShown_AndLeavesAutostartAlone()
    {
        await using var rig = new WizardRig();

        rig.Vm.Dismiss();

        Assert.True(rig.Settings.FirstRunDone);
        Assert.Empty(rig.Autostart.Calls);
    }

    [Fact]
    public async Task TheKeyIsWrittenOnce_HoweverTheWizardEnds()
    {
        await using var rig = new WizardRig();
        await rig.ConnectAndContinueAsync();
        rig.Vm.NextCommand.Execute(null);

        rig.Vm.FinishCommand.Execute(null);
        rig.Vm.Dismiss();   // the window's own OnClosed, after «Готово» closed it

        Assert.Equal(1, rig.Settings.FirstRunMarks);
    }

    [Fact]
    public async Task AClosedWizard_StopsFollowingTheMonitor()
    {
        await using var rig = new WizardRig();
        rig.Vm.Dismiss();

        await rig.Vm.StartCommand.ExecuteAsync(null);   // the proxy comes up, and the monitor says so
        await rig.Monitor.RefreshNowAsync();

        Assert.Equal("остановлен", rig.Vm.ProxyLine);
    }
}
