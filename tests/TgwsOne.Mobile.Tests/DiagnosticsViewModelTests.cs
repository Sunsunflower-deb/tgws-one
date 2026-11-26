using TgwsOne.Engine;

namespace TgwsOne.Mobile.Tests;

public class DiagnosticsViewModelTests
{
    private sealed class Rig
    {
        public FakeEngineControl Engine { get; } = new();
        public FakeDesyncControl Desync { get; } = new();
        public DiagnosticProbes Probes { get; } = new();
        public FakeDiagnosticsActions Actions { get; } = new();
        public long Sessions { get; set; } = 1;
        public DiagnosticsViewModel Vm { get; }

        public Rig()
        {
            var diagnostics = new Diagnostics(Engine, Desync, Probes, () => true, () => Sessions);
            Vm = new DiagnosticsViewModel(diagnostics, Actions);
        }
    }

    [Fact]
    public void Initially_ThereIsOneButton_AndNoResults()
    {
        var rig = new Rig();

        Assert.Equal("Запустить", rig.Vm.RunText);
        Assert.True(rig.Vm.ShowHint);
        Assert.False(rig.Vm.HasRun);
        Assert.False(rig.Vm.HasRows);
        Assert.False(rig.Vm.HasAction);
        Assert.Empty(rig.Vm.Rows);
    }

    [Fact]
    public async Task ARun_ShowsTheEightChecks_AndTheHealthyVerdict()
    {
        var rig = new Rig();

        await rig.Vm.RunCommand.ExecuteAsync(null);

        Assert.Equal(8, rig.Vm.Rows.Count);
        Assert.Equal("Движок прокси на месте", rig.Vm.Rows[0].Title);
        Assert.All(rig.Vm.Rows, row => Assert.Equal(Tone.Green, row.Tone));
        Assert.Equal("Всё в порядке.", rig.Vm.VerdictText);
        Assert.True(rig.Vm.HasRun);
        Assert.Equal("Проверить ещё раз", rig.Vm.RunText);
        Assert.False(rig.Vm.ShowHint);
        Assert.False(rig.Vm.HasAction);
    }

    [Fact]
    public async Task ASkippedCheck_CarriesTheReason()
    {
        var rig = new Rig();
        rig.Probes.TcpOk = 0;

        await rig.Vm.RunCommand.ExecuteAsync(null);

        Assert.Equal(Tone.Red, rig.Vm.Rows[4].Tone);
        foreach (int index in new[] { 5, 6 })
        {
            Assert.Equal(Tone.Grey, rig.Vm.Rows[index].Tone);
            Assert.True(rig.Vm.Rows[index].HasDetail);
            Assert.Equal("до серверов нет даже TCP", rig.Vm.Rows[index].Detail);
        }
    }

    [Fact]
    public async Task TheEnableBypassVerdict_RunsTheAction_AndChecksAgain()
    {
        var rig = new Rig();
        rig.Probes.TlsOk = 2;
        rig.Desync.Baseline = new FinderBaseline("auto", false);

        await rig.Vm.RunCommand.ExecuteAsync(null);
        Assert.Equal("TLS режется, обход выключен.", rig.Vm.VerdictText);
        Assert.Equal("Включить обход", rig.Vm.ActionText);
        Assert.True(rig.Vm.HasAction);

        int before = rig.Probes.Calls.Count;
        await rig.Vm.DoActionCommand.ExecuteAsync(null);

        Assert.Equal(1, rig.Actions.Enabled);
        Assert.True(rig.Probes.Calls.Count > before);   // the verdict is not left stale
    }

    [Fact]
    public async Task TheConnectVerdict_Connects_AndChecksAgain()
    {
        var rig = new Rig();
        rig.Engine.Status = new EngineProxyStatus(false, false, 1443, PortBusyByOther: false, Pid: null);
        await rig.Vm.RunCommand.ExecuteAsync(null);
        Assert.Equal("Подключить", rig.Vm.ActionText);

        await rig.Vm.DoActionCommand.ExecuteAsync(null);

        Assert.Equal(1, rig.Actions.Connects);
    }

    [Fact]
    public async Task TheChangePortVerdict_AsksThePageToOpenSettings()
    {
        var rig = new Rig();
        rig.Engine.Status = new EngineProxyStatus(false, false, 1443, PortBusyByOther: true, Pid: null);
        await rig.Vm.RunCommand.ExecuteAsync(null);
        Assert.Equal("Сменить порт", rig.Vm.ActionText);

        await rig.Vm.DoActionCommand.ExecuteAsync(null);

        Assert.Equal(1, rig.Actions.ChangePorts);
    }

    [Fact]
    public async Task OpenInTelegram_OpensTheLink_WithoutCheckingAgain()
    {
        var rig = new Rig { Sessions = 0 };
        await rig.Vm.RunCommand.ExecuteAsync(null);
        Assert.Equal("Открыть в Telegram", rig.Vm.ActionText);

        await rig.Vm.DoActionCommand.ExecuteAsync(null);

        Assert.Equal(1, rig.Actions.Opened);
    }

    [Fact]
    public async Task FindStrategy_AsksTheFinder()
    {
        var rig = new Rig();
        rig.Probes.TlsOk = 2;
        rig.Probes.BypassTlsOk = 1;
        await rig.Vm.RunCommand.ExecuteAsync(null);
        Assert.Equal("Подобрать стратегию", rig.Vm.ActionText);

        await rig.Vm.DoActionCommand.ExecuteAsync(null);

        Assert.Equal(1, rig.Actions.Found);
    }

    [Fact]
    public async Task AnActionThatFails_LeavesItsExplanationOnScreen()
    {
        var rig = new Rig();
        rig.Actions.Problem = "не удалось выполнить";
        rig.Engine.Status = new EngineProxyStatus(false, false, 1443, PortBusyByOther: false, Pid: null);
        await rig.Vm.RunCommand.ExecuteAsync(null);

        await rig.Vm.DoActionCommand.ExecuteAsync(null);

        Assert.Equal("не удалось выполнить", rig.Vm.ErrorText);
    }

    [Fact]
    public async Task ARunThatThrows_IsAnExplanation_NotAnEmptyPage()
    {
        var rig = new Rig();
        rig.Probes.BeforeProbe = _ => throw new InvalidOperationException("сеть недоступна");

        await rig.Vm.RunCommand.ExecuteAsync(null);

        Assert.Equal("Проверка не удалась: сеть недоступна", rig.Vm.ErrorText);
        Assert.False(rig.Vm.HasRun);
        Assert.False(rig.Vm.IsRunning);
    }
}
