using TgwsOne.Core;
using TgwsOne.Engine;
using TgwsOne.Engine.Desync;

namespace TgwsOne.Mobile.Tests;

public class RussianTests
{
    [Theory]
    [InlineData(0, "сбоев")]
    [InlineData(1, "сбой")]
    [InlineData(2, "сбоя")]
    [InlineData(3, "сбоя")]
    [InlineData(4, "сбоя")]
    [InlineData(5, "сбоев")]
    [InlineData(10, "сбоев")]
    [InlineData(11, "сбоев")]   // 11 to 14 are the "many" form whatever the last digit
    [InlineData(12, "сбоев")]
    [InlineData(14, "сбоев")]
    [InlineData(21, "сбой")]
    [InlineData(22, "сбоя")]
    [InlineData(25, "сбоев")]
    [InlineData(101, "сбой")]
    [InlineData(111, "сбоев")]
    [InlineData(112, "сбоев")]
    public void Plural_PicksTheFormByTheNumber(int n, string expected)
    {
        Assert.Equal(expected, Russian.Plural(n, "сбой", "сбоя", "сбоев"));
    }

    [Fact]
    public void EveryStatusPhrase_IsInRussian()
    {
        var status = new EngineProxyStatus(true, true, 1443, PortBusyByOther: false, Pid: 4242);
        var failed = new EngineStartResult(EngineStartOutcome.EngineMissing, 1443);

        List<string> texts =
        [
            MobileStatusText.HomeLine(status, null, 1).Text,
            MobileStatusText.HomeLine(status, null, 0).Text,
            MobileStatusText.HomeLine(status with { Listening = false, Running = false }, failed, 0).Text,
            MobileStatusText.HomeLine(status with { Listening = false, Running = false }, null, 0).Text,
            MobileStatusText.ProxyLine(status, "127.0.0.1").Text,
            MobileStatusText.ProxyLine(status with { Running = true, Listening = false }, "127.0.0.1").Text,
            MobileStatusText.ProxyLine(status with { Running = false, Listening = false, PortBusyByOther = true }, "127.0.0.1").Text,
            MobileStatusText.ProxyLine(status with { Running = false, Listening = false }, "127.0.0.1").Text,
            MobileStatusText.BypassLine(false, DesyncFinder.AutoId, []).Text,
            MobileStatusText.BypassLine(true, DesyncFinder.AutoId, []).Text,
            MobileStatusText.BypassLine(true, "split-sni", []).Text,
            .. BuiltInDesync.BuiltIn.Select(strategy => MobileStatusText.StrategyName(strategy.Id)),
        ];

        Assert.All(texts, text => Assert.Contains(text, character => character is >= 'А' and <= 'я'));
    }

    [Fact]
    public void EveryViewModel_ShowsItsLabelsInRussian()
    {
        var engine = new FakeEngineControl
        {
            Status = new EngineProxyStatus(false, false, 0, PortBusyByOther: false, Pid: null),
        };
        var settings = new FakeMobileSettings();
        var launcher = new FakeLauncher();
        var clipboard = new FakeClipboard();
        var control = new FakeBypassControl();
        var finderControl = new FakeDesyncControl();
        var finder = new FinderViewModel(new DesyncFinder(finderControl, new FinderProbes(finderControl), (_, _) => Task.CompletedTask));
        var diagnostics = new DiagnosticsViewModel(
            new Diagnostics(engine, control, new DiagnosticProbes(), () => true, () => 1), new FakeDiagnosticsActions());
        using var files = new TempFiles();
        var logs = new LogsViewModel(LogsViewModel.DefaultTabs(files.Storage), new FakeTicker(), clipboard, launcher);
        var settingsVm = new SettingsViewModel(settings);
        var connect = new ConnectViewModel(settings, launcher, clipboard);
        _ = finder;   // its page's writable strings are asserted in FinderViewModelTests

        string[] texts =
        [
            new HomeViewModel(engine, settings, () => 0).ButtonText,
            new HomeViewModel(engine, settings, () => 0).Headline,
            new HomeViewModel(engine, settings, () => 0).ProxyLine,
            new BypassViewModel(control, settings).ButtonText,
            BypassViewModel.EmptyAutoText,
            FinderViewModel.ConfirmText,
            FinderViewModel.AllRefusedVerdict,
            DesyncFinder.NoTelegramVerdict,
            DesyncFinder.NoBypassNeededVerdict,
            diagnostics.RunText,
            logs.Tabs[0].Title,
            logs.Tabs[1].Title,
            .. settingsVm.Strategies.Select(choice => choice.Name),
        ];

        Assert.All(texts, text => Assert.Contains(text, character => character is >= 'А' and <= 'я'));
        Assert.StartsWith("tg://proxy", connect.Link);
    }
}
