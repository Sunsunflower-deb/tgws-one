using TgwsOne.Core;
using TgwsOne.Core.Zapret;
using TgwsOne.Services;
using TgwsOne.ViewModels;
using TgwsOne.Views;

namespace TgwsOne.Tests;

public class DiagnosticsViewModelTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>Records what the verdict button asked for, and (like the real commands) changes the world so that the next check sees it.</summary>
    private sealed class FakeActions(Action<string>? effect = null) : IDiagnosticsActions
    {
        public List<string> Calls { get; } = [];
        public string? Problem { get; set; }

        private Task<string?> Do(string name)
        {
            Calls.Add(name);
            effect?.Invoke(name);
            return Task.FromResult(Problem);
        }

        public Task<string?> ConnectAsync() => Do("connect");
        public Task<string?> OpenInTelegramAsync() => Do("telegram");
        public Task<string?> EnableBypassAsync() => Do("bypass");
        public Task<string?> FindStrategyAsync() => Do("find");
    }

    private sealed class Rig
    {
        public FakeProxyControl Proxy { get; } = new();
        public FakeBypassService Service { get; } = new();
        public FakeNetInfo Net { get; } = new();
        public FixedProbes Probes { get; } = new();
        public FakeNavigator Navigator { get; } = new();
        public FakeActions Actions { get; }
        public DiagnosticsViewModel Vm { get; }

        public Rig(Action<Rig, string>? effect = null)
        {
            Proxy.Status = Samples.ProxyListening(1443);
            Net.Set(Samples.Tcp(1443, 50001));
            Actions = new FakeActions(effect is null ? null : name => effect(this, name));
            var diagnostics = new Diagnostics(Proxy, Service, Net, new FakeAppSettings(), Probes, Probes, Probes, () => true);
            Vm = new DiagnosticsViewModel(diagnostics, Navigator, Actions);
        }
    }

    private static async Task WaitUntil(Func<bool> condition)
    {
        DateTime limit = DateTime.UtcNow.AddSeconds(5);
        while (!condition())
        {
            if (DateTime.UtcNow > limit) throw new TimeoutException("the condition never became true");
            await Task.Delay(10, Ct);
        }
    }

    // ----- a run -----

    [Fact]
    public void BeforeTheFirstRun_ThereIsNothingToShow_AndTheButtonSaysRun()
    {
        var rig = new Rig();

        Assert.Empty(rig.Vm.Rows);
        Assert.False(rig.Vm.HasRows);
        Assert.Null(rig.Vm.VerdictText);
        Assert.False(rig.Vm.HasAction);
        Assert.Equal("Запустить", rig.Vm.RunText);
        Assert.Equal("Диагностика", rig.Vm.Title);
    }

    [Fact]
    public async Task ARun_FillsTheList_AndGivesTheVerdict()
    {
        var rig = new Rig();

        await rig.Vm.RunCommand.ExecuteAsync(null);

        Assert.Equal(7, rig.Vm.Rows.Count);
        Assert.True(rig.Vm.HasRows);
        Assert.Equal("Всё в порядке.", rig.Vm.VerdictText);
        Assert.False(rig.Vm.HasAction);
        Assert.False(rig.Vm.IsRunning);
        Assert.Equal("Проверить ещё раз", rig.Vm.RunText);
    }

    [Fact]
    public async Task TheRowsAppearOneByOne_WhileTheRunIsOn()
    {
        var rig = new Rig();
        var gate = new TaskCompletionSource();
        rig.Probes.BeforeProbe = async ct => await gate.Task.WaitAsync(ct);   // the network checks are held back

        Task run = rig.Vm.RunCommand.ExecuteAsync(null);
        await WaitUntil(() => rig.Vm.Rows.Count == 4);   // engine, proxy, Telegram, bypass: no network needed

        Assert.True(rig.Vm.IsRunning);
        Assert.Equal("Проверяю…", rig.Vm.RunText);
        Assert.False(rig.Vm.RunCommand.CanExecute(null));   // not twice at once
        Assert.Null(rig.Vm.VerdictText);

        gate.SetResult();
        await run;

        Assert.Equal(7, rig.Vm.Rows.Count);
        Assert.False(rig.Vm.IsRunning);
    }

    [Fact]
    public async Task ANewRun_ReplacesTheOldList()
    {
        var rig = new Rig();
        await rig.Vm.RunCommand.ExecuteAsync(null);
        rig.Probes.TlsOk = 0;

        await rig.Vm.RunCommand.ExecuteAsync(null);

        Assert.Equal(7, rig.Vm.Rows.Count);
        Assert.Equal("TLS до серверов Telegram: 0/5", rig.Vm.Rows[5].Title);
        Assert.Equal("Провайдер режет Telegram, обход не справляется.", rig.Vm.VerdictText);
    }

    [Theory]
    [InlineData("Ok", IconPaths.Check, "Green")]
    [InlineData("Warn", IconPaths.Warning, "Amber")]
    [InlineData("Fail", IconPaths.Error, "Red")]
    [InlineData("Skipped", IconPaths.Dash, "Grey")]
    public void EachStateHasItsMark(string state, string path, string tone)
    {
        var row = new CheckRowViewModel(new CheckResult("x", Enum.Parse<CheckState>(state)));

        Assert.Equal((path, Enum.Parse<Tone>(tone)), (row.IconPath, row.Tone));
    }

    // ----- the button under the verdict -----

    [Fact]
    public async Task EachVerdictAction_HasItsLabel()
    {
        var rig = new Rig();
        var labels = new List<(string Verdict, string? Button)>();

        async Task Check(Action arrange)
        {
            arrange();
            await rig.Vm.RunCommand.ExecuteAsync(null);
            labels.Add((rig.Vm.VerdictText!, rig.Vm.ActionText));
        }

        await Check(() => rig.Proxy.Status = new ProxyStatus(false, false, 1443, true, null));
        await Check(() => rig.Proxy.Status = Samples.ProxyStopped);
        await Check(() => { rig.Proxy.Status = Samples.ProxyListening(); rig.Probes.TlsOk = 0; rig.Service.Enabled = false; });
        await Check(() => rig.Service.Enabled = true);
        await Check(() => { rig.Probes.TlsOk = 5; rig.Probes.CloudflareOk = true; rig.Net.Set(); });
        await Check(() => rig.Net.Set(Samples.Tcp(1443, 50001)));

        Assert.Equal(
        [
            ("Порт 1443 занят другим приложением.", "Сменить порт"),
            ("Прокси выключен.", "Подключить"),
            ("Провайдер режет Telegram. Включите обход DPI.", "Включить обход"),
            ("Провайдер режет Telegram, обход не справляется.", "Подобрать стратегию"),
            ("Прокси работает, но Telegram к нему не подключён.", "Открыть в Telegram"),
            ("Всё в порядке.", null),
        ], labels);
    }

    [Fact]
    public async Task ChangePort_GoesToSettings()
    {
        var rig = new Rig();
        rig.Proxy.Status = new ProxyStatus(false, false, 1443, true, null);
        await rig.Vm.RunCommand.ExecuteAsync(null);

        await rig.Vm.DoActionCommand.ExecuteAsync(null);

        Assert.Equal([(AppPage.Settings, false)], rig.Navigator.Shown);
        Assert.Empty(rig.Actions.Calls);
    }

    [Fact]
    public async Task Connect_RunsTheHomeCommand_ThenChecksAgain()
    {
        var rig = new Rig((r, name) => { if (name == "connect") r.Proxy.Status = Samples.ProxyListening(); });
        rig.Proxy.Status = Samples.ProxyStopped;
        await rig.Vm.RunCommand.ExecuteAsync(null);
        Assert.Equal("Прокси выключен.", rig.Vm.VerdictText);

        await rig.Vm.DoActionCommand.ExecuteAsync(null);

        Assert.Equal(["connect"], rig.Actions.Calls);
        Assert.Equal("Всё в порядке.", rig.Vm.VerdictText);   // the verdict was renewed, not left stale
        Assert.Equal(7, rig.Vm.Rows.Count);
    }

    [Fact]
    public async Task EnableBypass_SwitchesItOn_ThenChecksAgain()
    {
        var rig = new Rig((r, name) => { if (name == "bypass") { r.Service.Enabled = true; r.Probes.TlsOk = 5; } });
        rig.Probes.TlsOk = 0;
        rig.Service.Enabled = false;
        await rig.Vm.RunCommand.ExecuteAsync(null);
        Assert.Equal("Включить обход", rig.Vm.ActionText);

        await rig.Vm.DoActionCommand.ExecuteAsync(null);

        Assert.Equal(["bypass"], rig.Actions.Calls);
        Assert.Equal("Всё в порядке.", rig.Vm.VerdictText);
    }

    [Fact]
    public async Task FindStrategy_OpensTheFinder_ThenChecksAgain()
    {
        var rig = new Rig((r, name) => { if (name == "find") r.Probes.TlsOk = 5; });
        rig.Probes.TlsOk = 0;
        await rig.Vm.RunCommand.ExecuteAsync(null);

        await rig.Vm.DoActionCommand.ExecuteAsync(null);

        Assert.Equal(["find"], rig.Actions.Calls);
        Assert.Equal("Всё в порядке.", rig.Vm.VerdictText);
    }

    [Fact]
    public async Task OpenInTelegram_OpensIt_AndDoesNotCheckAgain_ForTheUserStillHasToAnswerInTelegram()
    {
        var rig = new Rig();
        rig.Net.Set();
        await rig.Vm.RunCommand.ExecuteAsync(null);
        int probesBefore = rig.Probes.Downloads;

        await rig.Vm.DoActionCommand.ExecuteAsync(null);

        Assert.Equal(["telegram"], rig.Actions.Calls);
        Assert.Equal(probesBefore, rig.Probes.Downloads);   // no second run
        Assert.Equal("Прокси работает, но Telegram к нему не подключён.", rig.Vm.VerdictText);
    }

    [Fact]
    public async Task AnActionThatFails_ShowsWhy_AfterTheNewCheck()
    {
        var rig = new Rig();
        rig.Probes.TlsOk = 0;
        rig.Service.Enabled = false;
        rig.Actions.Problem = "Обход DPI не включился: проверка параметров не прошла";
        await rig.Vm.RunCommand.ExecuteAsync(null);

        await rig.Vm.DoActionCommand.ExecuteAsync(null);

        Assert.Equal("Обход DPI не включился: проверка параметров не прошла", rig.Vm.ErrorText);
        Assert.Equal("Провайдер режет Telegram. Включите обход DPI.", rig.Vm.VerdictText);   // still true: the check ran again, and said so
    }

    [Fact]
    public async Task WithoutAVerdictAction_ThereIsNothingToRun()
    {
        var rig = new Rig();
        await rig.Vm.RunCommand.ExecuteAsync(null);

        await rig.Vm.DoActionCommand.ExecuteAsync(null);

        Assert.Empty(rig.Actions.Calls);
        Assert.Empty(rig.Navigator.Shown);
    }

    // ----- being asked to start -----

    [Fact]
    public async Task ShowingTheDiagnosticsPage_WithTheCheckRequested_StartsARun()
    {
        var rig = new Rig();
        var window = new MainWindowViewModel(_ =>
        [
            MainWindowViewModel.Placeholder(AppPage.Home), MainWindowViewModel.Placeholder(AppPage.Bypass), rig.Vm,
            MainWindowViewModel.Placeholder(AppPage.Logs), MainWindowViewModel.Placeholder(AppPage.Settings),
        ]);

        window.Show(AppPage.Diagnostics, startCheck: true);
        await WaitUntil(() => rig.Vm.HasRun);

        Assert.Same(rig.Vm, window.SelectedPage);
        Assert.Equal(7, rig.Vm.Rows.Count);
    }

    [Fact]
    public async Task ShowingTheDiagnosticsPage_WithoutTheCheckRequested_DoesNotRunAnything()
    {
        var rig = new Rig();
        var window = new MainWindowViewModel(_ =>
        [
            MainWindowViewModel.Placeholder(AppPage.Home), MainWindowViewModel.Placeholder(AppPage.Bypass), rig.Vm,
            MainWindowViewModel.Placeholder(AppPage.Logs), MainWindowViewModel.Placeholder(AppPage.Settings),
        ]);

        window.Show(AppPage.Diagnostics);
        await Task.Delay(100, Ct);

        Assert.False(rig.Vm.HasRun);
        Assert.Empty(rig.Vm.Rows);
    }
}
