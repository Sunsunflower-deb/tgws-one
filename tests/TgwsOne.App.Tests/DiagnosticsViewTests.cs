using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using TgwsOne.Services;
using TgwsOne.ViewModels;
using TgwsOne.Views;

namespace TgwsOne.Tests;

/// <summary>Диагностика drawn on the headless platform.</summary>
public class DiagnosticsViewTests
{
    private sealed class NoActions : IDiagnosticsActions
    {
        public Task<string?> ConnectAsync() => Task.FromResult<string?>(null);
        public Task<string?> OpenInTelegramAsync() => Task.FromResult<string?>(null);
        public Task<string?> EnableBypassAsync() => Task.FromResult<string?>(null);
        public Task<string?> FindStrategyAsync() => Task.FromResult<string?>(null);
    }

    private sealed class Rig
    {
        public FakeProxyControl Proxy { get; } = new();
        public FakeBypassService Service { get; } = new();
        public FakeNetInfo Net { get; } = new();
        public FixedProbes Probes { get; } = new();
        public DiagnosticsViewModel Vm { get; }
        public DiagnosticsView View { get; }

        public Rig()
        {
            Proxy.Status = Samples.ProxyListening(1443);
            Net.Set(Samples.Tcp(1443, 50001));
            Vm = new DiagnosticsViewModel(new Diagnostics(Proxy, Service, Net, new FakeAppSettings(), Probes, Probes, Probes, () => true),
                                          new FakeNavigator(), new NoActions());
            View = new DiagnosticsView { DataContext = Vm };
            new Window { Content = View, Width = 820, Height = 700 }.Show();
            Dispatcher.UIThread.RunJobs();
        }

        public T Find<T>(string name) where T : Control
            => View.FindControl<T>(name) ?? throw new InvalidOperationException($"no control named {name}");

        public async Task RunAsync()
        {
            await Vm.RunCommand.ExecuteAsync(null);
            Dispatcher.UIThread.RunJobs();
        }
    }

    [AvaloniaFact]
    public void BeforeARun_ThereIsAHint_AndNoList()
    {
        var rig = new Rig();

        Assert.Equal("Запустить", rig.Find<Button>("RunButton").Content);
        Assert.True(rig.Find<TextBlock>("HintText").IsVisible);
        Assert.False(rig.Find<Border>("ChecksPanel").IsVisible);
        Assert.False(rig.Find<StackPanel>("VerdictPanel").IsVisible);
        Assert.False(rig.Find<ProgressBar>("RunProgress").IsVisible);
    }

    [AvaloniaFact]
    public async Task AfterARun_EveryCheckIsAListedWithItsMark_AndTheVerdictIsShown()
    {
        var rig = new Rig();
        rig.Probes.TlsOk = 0;   // the spec's example: the provider cuts Telegram
        rig.Net.Set();

        await rig.RunAsync();

        var list = rig.Find<ItemsControl>("CheckList");
        string[] texts = [.. list.GetVisualDescendants().OfType<TextBlock>().Select(t => t.Text ?? "")];
        Assert.Contains("Движок прокси на месте", texts);
        Assert.Contains("Прокси слушает 127.0.0.1:1443", texts);
        Assert.Contains("Telegram не подключён к прокси", texts);
        Assert.Contains("TLS до серверов Telegram: 0/5", texts);
        Assert.Contains("Cloudflare: 64 КБ за 0,8 с", texts);

        var icons = list.GetVisualDescendants().OfType<Icon>().ToList();
        Assert.Equal(7, icons.Count);
        Assert.Equal([Tone.Green, Tone.Green, Tone.Amber, Tone.Green, Tone.Green, Tone.Red, Tone.Green], icons.Select(i => i.Tone!.Value));
        Assert.Equal([IconPaths.Check, IconPaths.Check, IconPaths.Warning, IconPaths.Check, IconPaths.Check, IconPaths.Error, IconPaths.Check],
                     icons.Select(i => i.Data));

        Assert.True(rig.Find<StackPanel>("VerdictPanel").IsVisible);
        Assert.Equal("Провайдер режет Telegram, обход не справляется.", rig.Find<TextBlock>("VerdictText").Text);
        Assert.True(rig.Find<Button>("ActionButton").IsVisible);
        Assert.Equal("Подобрать стратегию", rig.Find<Button>("ActionButton").Content);
        Assert.Equal("Проверить ещё раз", rig.Find<Button>("RunButton").Content);
        Assert.False(rig.Find<TextBlock>("HintText").IsVisible);
    }

    [AvaloniaFact]
    public async Task AHealthyRun_HasAVerdict_ButNoActionButton()
    {
        var rig = new Rig();

        await rig.RunAsync();

        Assert.Equal("Всё в порядке.", rig.Find<TextBlock>("VerdictText").Text);
        Assert.False(rig.Find<Button>("ActionButton").IsVisible);
    }

    [AvaloniaFact]
    public async Task WhileItRuns_ThereIsAProgressBar_AndTheButtonSaysSo()
    {
        var rig = new Rig();
        var gate = new TaskCompletionSource();
        rig.Probes.BeforeProbe = async ct => await gate.Task.WaitAsync(ct);

        Task run = rig.Vm.RunCommand.ExecuteAsync(null);
        while (rig.Vm.Rows.Count < 4) await Task.Delay(10, TestContext.Current.CancellationToken);
        Dispatcher.UIThread.RunJobs();

        Assert.True(rig.Find<ProgressBar>("RunProgress").IsVisible);
        Assert.Equal("Проверяю…", rig.Find<Button>("RunButton").Content);
        Assert.False(rig.Find<Button>("RunButton").IsEffectivelyEnabled);   // a run is not started twice
        Assert.Equal(4, rig.Find<ItemsControl>("CheckList").GetVisualDescendants().OfType<Icon>().Count());   // the first four are there already

        gate.SetResult();
        await run;
        Dispatcher.UIThread.RunJobs();

        Assert.False(rig.Find<ProgressBar>("RunProgress").IsVisible);
    }
}
