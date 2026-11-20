using System.ComponentModel;
using TgwsOne.Core.Zapret;
using TgwsOne.Services;
using TgwsOne.ViewModels;

namespace TgwsOne.Tests;

public class BypassViewModelTests
{
    private static AutoHost Host(string host, string strategy, int failures = 0) => new(host, strategy, failures);

    // ----- strategies -----

    [Fact]
    public async Task Strategies_AreListedWithTheirDisplayNames_AndTheCurrentOneIsSelected()
    {
        await using var rig = new BypassRig();
        rig.Service.StrategyIds.Add("custom:my-isp");
        rig.Service.Strategy = "split";

        await rig.OpenAsync();

        Assert.Equal(["Авто (рекомендуется)", "fake-tls", "split", "split-stealth", "aggressive", "my-isp (своя)"],
                     rig.Bypass.Strategies.Select(s => s.Name));
        Assert.Equal(["split"], rig.Bypass.Strategies.Where(s => s.IsSelected).Select(s => s.Id));
        Assert.Equal("split", rig.Bypass.CurrentStrategy);
    }

    [Fact]
    public async Task ChoosingAStrategy_SendsUse_AndSelectsIt()
    {
        await using var rig = new BypassRig();
        await rig.OpenAsync();
        StrategyOption fake = rig.Bypass.Strategies.Single(s => s.Id == "fake-tls");

        await fake.ChooseCommand.ExecuteAsync(null);

        Assert.Equal("fake-tls", rig.Service.Sent.Single(r => r.Cmd == "use").Strategy);
        Assert.Equal(["fake-tls"], rig.Bypass.Strategies.Where(s => s.IsSelected).Select(s => s.Id));
        Assert.Equal("fake-tls", rig.Bypass.CurrentStrategy);
        Assert.Null(rig.Bypass.ErrorText);
    }

    [Fact]
    public async Task ChoosingTheCurrentStrategy_SendsNothing()
    {
        await using var rig = new BypassRig();
        await rig.OpenAsync();

        await rig.Bypass.Strategies.Single(s => s.Id == "auto").ChooseCommand.ExecuteAsync(null);

        Assert.Equal(0, rig.Service.Count("use"));
    }

    [Fact]
    public async Task ARefusedStrategy_ShowsTheServicesMessage_AndTheSelectionSnapsBack()
    {
        await using var rig = new BypassRig();
        rig.Service.UseRefusal = "проверка параметров не прошла: bad option";
        await rig.OpenAsync();
        StrategyOption aggressive = rig.Bypass.Strategies.Single(s => s.Id == "aggressive");
        var repushed = new List<string>();
        foreach (StrategyOption o in rig.Bypass.Strategies)
            o.PropertyChanged += (_, e) => { if (e.PropertyName == nameof(StrategyOption.IsSelected)) repushed.Add(o.Id); };

        await aggressive.ChooseCommand.ExecuteAsync(null);

        Assert.Equal("проверка параметров не прошла: bad option", rig.Bypass.ErrorText);
        Assert.Equal("auto", rig.Bypass.CurrentStrategy);
        Assert.Equal(["auto"], rig.Bypass.Strategies.Where(s => s.IsSelected).Select(s => s.Id));
        // The radio that was clicked has checked itself on screen: every option is told its value again, so it goes back.
        Assert.Contains("aggressive", repushed);
        Assert.Contains("auto", repushed);
    }

    [Fact]
    public async Task ASuccessfulChoice_ClearsTheEarlierError()
    {
        await using var rig = new BypassRig();
        rig.Service.UseRefusal = "не вышло";
        await rig.OpenAsync();
        await rig.Bypass.Strategies.Single(s => s.Id == "split").ChooseCommand.ExecuteAsync(null);
        Assert.Equal("не вышло", rig.Bypass.ErrorText);

        rig.Service.UseRefusal = null;
        await rig.Bypass.Strategies.Single(s => s.Id == "split").ChooseCommand.ExecuteAsync(null);

        Assert.Null(rig.Bypass.ErrorText);
        Assert.Equal("split", rig.Bypass.CurrentStrategy);
    }

    // ----- the switch -----

    [Fact]
    public async Task TheSwitch_TurnsTheBypassOffAndOn()
    {
        await using var rig = new BypassRig();
        await rig.OpenAsync();
        Assert.True(rig.Bypass.BypassOn);

        await rig.Bypass.ToggleBypassCommand.ExecuteAsync(null);
        Assert.False(rig.Bypass.BypassOn);
        Assert.Equal(1, rig.Service.Count("off"));

        await rig.Bypass.ToggleBypassCommand.ExecuteAsync(null);
        Assert.True(rig.Bypass.BypassOn);
        Assert.Equal(1, rig.Service.Count("on"));
    }

    [Fact]
    public async Task ARefusedSwitch_ShowsWhy_AndTheSwitchGoesBack()
    {
        await using var rig = new BypassRig();
        rig.Service.Enabled = false;
        rig.Service.SwitchRefusal = "проверка параметров не прошла";
        await rig.OpenAsync();
        Assert.False(rig.Bypass.BypassOn);
        var repushed = new List<string?>();
        rig.Bypass.PropertyChanged += (_, e) => repushed.Add(e.PropertyName);

        await rig.Bypass.ToggleBypassCommand.ExecuteAsync(null);

        Assert.False(rig.Bypass.BypassOn);
        Assert.Equal("проверка параметров не прошла", rig.Bypass.ErrorText);
        Assert.Contains(nameof(BypassViewModel.BypassOn), repushed);   // the clicked switch is told again
    }

    [Fact]
    public async Task TheStatusLine_ComesFromTheServicesState()
    {
        await using var rig = new BypassRig();
        rig.Service.State = "error";
        rig.Service.LastError = "параметры не подходят\nподробности";

        await rig.OpenAsync();

        Assert.Equal("ошибка: параметры не подходят", rig.Bypass.StatusLine);
        Assert.Equal(Tone.Red, rig.Bypass.StatusTone);
    }

    // ----- what Авто chose -----

    [Fact]
    public async Task TheAutoTable_ListsWhatAutoChosePerHost()
    {
        await using var rig = new BypassRig();
        rig.Service.Auto.AddRange([Host("kws2.web.telegram.org", "split", 1), Host("kws4.web.telegram.org", "fake-tls", 0),
                                   Host("kws1.web.telegram.org", "aggressive", 2), Host("kws3.web.telegram.org", "split-stealth", 5)]);

        await rig.OpenAsync();

        Assert.True(rig.Bypass.ShowAuto);
        Assert.False(rig.Bypass.AutoIsEmpty);
        Assert.Equal([("kws2.web.telegram.org", "split", "1 сбой"), ("kws4.web.telegram.org", "fake-tls", "—"),
                      ("kws1.web.telegram.org", "aggressive", "2 сбоя"), ("kws3.web.telegram.org", "split-stealth", "5 сбоев")],
                     rig.Bypass.AutoRows.Select(r => (r.Host, r.StrategyName, r.Failures)));
    }

    [Fact]
    public async Task TheAutoTable_WithoutData_SaysToOpenTelegram()
    {
        await using var rig = new BypassRig();

        await rig.OpenAsync();

        Assert.True(rig.Bypass.ShowAuto);
        Assert.True(rig.Bypass.AutoIsEmpty);
        Assert.Equal("Пока нет данных — откройте Telegram, Авто начнёт выбирать после первых соединений", BypassViewModel.EmptyAutoText);
    }

    [Fact]
    public async Task TheAutoTable_IsHidden_ForAnyOtherStrategy()
    {
        await using var rig = new BypassRig();
        rig.Service.Auto.Add(Host("kws2.web.telegram.org", "split"));
        await rig.OpenAsync();
        Assert.True(rig.Bypass.ShowAuto);

        await rig.Bypass.Strategies.Single(s => s.Id == "split").ChooseCommand.ExecuteAsync(null);

        Assert.False(rig.Bypass.ShowAuto);
        Assert.False(rig.Bypass.AutoIsEmpty);
        Assert.Empty(rig.Bypass.AutoRows);
    }

    [Fact]
    public async Task TheAutoTable_FollowsTheNextPoll()
    {
        await using var rig = new BypassRig();
        await rig.OpenAsync();
        Assert.Empty(rig.Bypass.AutoRows);

        rig.Service.Auto.Add(Host("kws2.web.telegram.org", "split", 1));
        await rig.Monitor.RefreshNowAsync();

        Assert.Equal(["kws2.web.telegram.org"], rig.Bypass.AutoRows.Select(r => r.Host));
    }

    // ----- exclusions -----

    [Fact]
    public async Task Exclusions_AreSummarisedOnOneLine()
    {
        await using var rig = new BypassRig();
        rig.Service.Hosts.Add("sberbank.ru");
        rig.Service.Ips.Add("203.0.113.0/24");

        await rig.OpenAsync();

        Assert.Equal("sberbank.ru · 203.0.113.0/24", rig.Bypass.ExclusionsSummary);
    }

    [Fact]
    public async Task Exclusions_None_SaysSo()
    {
        await using var rig = new BypassRig();

        await rig.OpenAsync();

        Assert.Equal("нет исключений", rig.Bypass.ExclusionsSummary);
    }

    [Fact]
    public async Task Exclusions_ALongList_IsCutShort()
    {
        await using var rig = new BypassRig();
        rig.Service.Hosts.AddRange(["a.example.com", "b.example.com", "c.example.com", "d.example.com", "e.example.com", "f.example.com"]);
        rig.Service.Ips.Add("10.0.0.0/8");

        await rig.OpenAsync();

        Assert.Equal("a.example.com · b.example.com · c.example.com · d.example.com · ещё 3", rig.Bypass.ExclusionsSummary);
    }

    [Fact]
    public async Task EditingExclusions_FillsTheTwoBoxes_OneEntryPerLine()
    {
        await using var rig = new BypassRig();
        rig.Service.Hosts.AddRange(["sberbank.ru", "gosuslugi.ru"]);
        rig.Service.Ips.Add("203.0.113.0/24");
        await rig.OpenAsync();

        rig.Bypass.EditExclusionsCommand.Execute(null);

        Assert.True(rig.Bypass.IsEditingExclusions);
        Assert.Equal("sberbank.ru\ngosuslugi.ru", rig.Bypass.HostsText);
        Assert.Equal("203.0.113.0/24", rig.Bypass.IpsText);
    }

    [Fact]
    public async Task SavingExclusions_SendsBothLists_AndClosesTheEditor()
    {
        await using var rig = new BypassRig();
        await rig.OpenAsync();
        rig.Bypass.EditExclusionsCommand.Execute(null);
        rig.Bypass.HostsText = "Sberbank.RU\n gosuslugi.ru, mail.ru";   // untidy on purpose: lines, commas, spaces
        rig.Bypass.IpsText = "203.0.113.0/24";

        await rig.Bypass.SaveExclusionsCommand.ExecuteAsync(null);

        ControlRequest sent = rig.Service.Sent.Single(r => r.Cmd == "set-exclusions");
        Assert.Equal(["Sberbank.RU", "gosuslugi.ru", "mail.ru"], sent.Hosts);
        Assert.Equal(["203.0.113.0/24"], sent.Ips);
        Assert.False(rig.Bypass.IsEditingExclusions);
        Assert.Null(rig.Bypass.InvalidText);
        Assert.Equal("sberbank.ru · gosuslugi.ru · mail.ru · 203.0.113.0/24", rig.Bypass.ExclusionsSummary);   // as the service normalised them
    }

    [Fact]
    public async Task InvalidEntries_AreListed_AndNothingIsSaved_UntilTheyAreFixed()
    {
        await using var rig = new BypassRig();
        rig.Service.Hosts.Add("keep.example.com");
        await rig.OpenAsync();
        rig.Bypass.EditExclusionsCommand.Execute(null);
        rig.Bypass.HostsText = "good.example.com\nbad!host";
        rig.Bypass.IpsText = "10.0.0.1";

        await rig.Bypass.SaveExclusionsCommand.ExecuteAsync(null);

        Assert.True(rig.Bypass.IsEditingExclusions);   // still open
        Assert.Contains("bad!host", rig.Bypass.InvalidText);
        Assert.Contains("ничего не сохраняется", rig.Bypass.InvalidText);
        Assert.Equal(["keep.example.com"], rig.Service.Hosts);   // untouched
        Assert.Equal(1, rig.Service.Count("set-exclusions"));   // sent once, not retried behind the user's back

        rig.Bypass.HostsText = "good.example.com";
        await rig.Bypass.SaveExclusionsCommand.ExecuteAsync(null);

        Assert.False(rig.Bypass.IsEditingExclusions);
        Assert.Null(rig.Bypass.InvalidText);
        Assert.Equal(["good.example.com"], rig.Service.Hosts);
        Assert.Equal(2, rig.Service.Count("set-exclusions"));
    }

    [Fact]
    public async Task CancelingTheEditor_KeepsWhatWasThere()
    {
        await using var rig = new BypassRig();
        rig.Service.Hosts.Add("sberbank.ru");
        await rig.OpenAsync();
        rig.Bypass.EditExclusionsCommand.Execute(null);
        rig.Bypass.HostsText = "something.else";

        rig.Bypass.CancelExclusionsCommand.Execute(null);

        Assert.False(rig.Bypass.IsEditingExclusions);
        Assert.Equal("sberbank.ru", rig.Bypass.ExclusionsSummary);
        Assert.Equal(0, rig.Service.Count("set-exclusions"));
    }

    // ----- no service -----

    [Fact]
    public async Task WithoutAService_ThePageIsOff_AndSaysWhy()
    {
        await using var rig = new BypassRig();
        rig.Service.Unavailable = true;

        await rig.Monitor.RefreshNowAsync();
        await rig.Bypass.LoadCommand.ExecuteAsync(null);   // must not throw

        Assert.False(rig.Bypass.ServiceReachable);
        Assert.False(rig.Bypass.CanChange);
        Assert.False(rig.Bypass.ToggleBypassCommand.CanExecute(null));
        Assert.False(rig.Bypass.SaveExclusionsCommand.CanExecute(null));
        Assert.Equal("Обход DPI не установлен — нужен администратор", BypassViewModel.UnavailableText);
        Assert.Equal("не установлен — нужен администратор", rig.Bypass.StatusLine);
    }

    [Fact]
    public async Task TheServiceComingBack_TurnsThePageOn()
    {
        await using var rig = new BypassRig();
        rig.Service.Unavailable = true;
        await rig.Monitor.RefreshNowAsync();
        Assert.False(rig.Bypass.CanChange);

        rig.Service.Unavailable = false;
        await rig.Monitor.RefreshNowAsync();

        Assert.True(rig.Bypass.CanChange);
        Assert.True(rig.Bypass.ToggleBypassCommand.CanExecute(null));
    }

    [Fact]
    public async Task WhileACommandRuns_TheControlsAreOff()
    {
        await using var rig = new BypassRig();
        await rig.OpenAsync();
        var changes = new List<(string? Property, bool CanChange)>();
        rig.Bypass.PropertyChanged += (_, e) => changes.Add((e.PropertyName, rig.Bypass.CanChange));

        await rig.Bypass.ToggleBypassCommand.ExecuteAsync(null);

        Assert.Contains((nameof(BypassViewModel.CanChange), false), changes);   // it went off while the command was out
        Assert.True(rig.Bypass.CanChange);                                       // and is back on
    }

    // ----- loading when shown -----

    [Fact]
    public async Task Load_AsksForStrategiesAndExclusions()
    {
        await using var rig = new BypassRig();
        await rig.Monitor.RefreshNowAsync();

        await rig.Bypass.LoadCommand.ExecuteAsync(null);

        Assert.Equal(1, rig.Service.Count("strategies"));
        Assert.Equal(1, rig.Service.Count("exclusions"));
    }

    private sealed class ProbePage() : PageViewModel("probe", "")
    {
        public int Shown { get; private set; }
        public override void OnShown() => Shown++;
    }

    [Fact]
    public void ShowingAPage_CallsItsOnShown_OncePerVisit()
    {
        var probe = new ProbePage();
        var vm = new MainWindowViewModel(nav =>
        [
            MainWindowViewModel.Placeholder(AppPage.Home), MainWindowViewModel.Placeholder(AppPage.Bypass), probe,
            MainWindowViewModel.Placeholder(AppPage.Logs), MainWindowViewModel.Placeholder(AppPage.Settings),
        ]);
        Assert.Equal(0, probe.Shown);

        vm.Show(AppPage.Diagnostics);
        vm.Show(AppPage.Diagnostics);   // already there: nothing changes
        Assert.Equal(1, probe.Shown);

        vm.Show(AppPage.Home);
        vm.Show(AppPage.Diagnostics);
        Assert.Equal(2, probe.Shown);
    }
}
