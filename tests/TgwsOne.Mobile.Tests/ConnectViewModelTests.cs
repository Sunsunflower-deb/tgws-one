namespace TgwsOne.Mobile.Tests;

public class ConnectViewModelTests
{
    private sealed class Rig
    {
        public FakeMobileSettings Settings { get; } = new();
        public FakeLauncher Launcher { get; } = new();
        public FakeClipboard Clipboard { get; } = new();
        public FakeShareService Share { get; } = new();
        public ConnectViewModel Vm { get; set; }

        public Rig()
        {
            Vm = new ConnectViewModel(Settings, Launcher, Clipboard, Share);
        }
    }

    [Fact]
    public void TheLink_IsTheSavedOne()
    {
        var rig = new Rig();

        Assert.Equal(rig.Settings.Link, rig.Vm.Link);
        Assert.StartsWith("tg://proxy", rig.Vm.Link);
    }

    [Fact]
    public void Refresh_ReadsTheLinkAgain()
    {
        var rig = new Rig();
        rig.Settings.Link = "tg://proxy?server=127.0.0.1&port=1444&secret=ddabcd";

        rig.Vm.Refresh();

        Assert.Equal(rig.Settings.Link, rig.Vm.Link);
    }

    [Fact]
    public async Task OpenInTelegram_HandsTheLinkToTheSystem()
    {
        var rig = new Rig();

        await rig.Vm.OpenInTelegramCommand.ExecuteAsync(null);

        Assert.Equal([rig.Settings.Link], rig.Launcher.Opened);
        Assert.Null(rig.Vm.ErrorText);
        Assert.Null(rig.Vm.Notice);
    }

    [Fact]
    public async Task WhenTelegramIsMissing_ItSaysToCopyTheLink()
    {
        var rig = new Rig();
        rig.Launcher.Succeeds = false;

        await rig.Vm.OpenInTelegramCommand.ExecuteAsync(null);

        Assert.Equal("Не удалось открыть Telegram. Скопируйте ссылку и вставьте её в Telegram вручную.", rig.Vm.ErrorText);
    }

    [Fact]
    public async Task Copy_PutsTheLinkOnTheClipboard()
    {
        var rig = new Rig();

        await rig.Vm.CopyLinkCommand.ExecuteAsync(null);

        Assert.Equal(rig.Settings.Link, rig.Clipboard.Text);
        Assert.Equal("Ссылка скопирована", rig.Vm.Notice);
    }

    [Fact]
    public async Task Share_HandsTheLinkToTheSystemChooser()
    {
        var rig = new Rig();

        await rig.Vm.ShareLinkCommand.ExecuteAsync(null);

        Assert.Equal([rig.Settings.Link], rig.Share.Shared);
        Assert.Equal("Ссылка отправлена", rig.Vm.Notice);
    }
}
