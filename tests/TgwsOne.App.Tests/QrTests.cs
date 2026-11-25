using System.Net;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Avalonia.VisualTree;
using SkiaSharp;
using TgwsOne.Core;
using TgwsOne.Services;
using TgwsOne.ViewModels;
using TgwsOne.Views;
using ZXing;
using ZXing.Common;

namespace TgwsOne.Tests;

public class QrTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private const string Secret = "0123456789abcdef0123456789abcdef";

    /// <summary>Reads the picture the way a phone's camera would: decodes the PNG to pixels and looks for a QR code in them.</summary>
    private static string? Scan(byte[] png)
    {
        using SKBitmap bitmap = SKBitmap.Decode(png);
        using SKBitmap rgba = bitmap.Copy(SKColorType.Rgba8888);
        var source = new RGBLuminanceSource(rgba.Bytes, rgba.Width, rgba.Height, RGBLuminanceSource.BitmapFormat.RGBA32);
        var reader = new BarcodeReaderGeneric { Options = new DecodingOptions { PossibleFormats = [BarcodeFormat.QR_CODE], TryHarder = true } };
        return reader.Decode(source)?.Text;
    }

    // ----- the payload -----

    [Fact]
    public void ThePayload_IsExactlyTheWebLinkTelegramOpensFromACamera()
    {
        string link = QrPayload.Build(IPAddress.Parse("192.168.18.20"), 1443, Secret);

        Assert.Equal("https://t.me/proxy?server=192.168.18.20&port=1443&secret=dd0123456789abcdef0123456789abcdef", link);
    }

    [Fact]
    public void TheSecretCarriesTheDdPrefix_AndThePortIsPlain()
    {
        string link = QrPayload.Build(IPAddress.Parse("10.0.0.7"), 65535, new string('f', 32));

        Assert.EndsWith("&port=65535&secret=dd" + new string('f', 32), link);
        Assert.StartsWith("https://t.me/proxy?server=10.0.0.7&", link);
    }

    // ----- the picture -----

    [Fact]
    public void ThePng_IsASquarePicture_OfAReadableSize()
    {
        byte[] png = QrPayload.Png(QrPayload.Build(IPAddress.Parse("192.168.18.20"), 1443, Secret));

        Assert.Equal([0x89, (byte)'P', (byte)'N', (byte)'G'], png[..4]);
        using SKBitmap bitmap = SKBitmap.Decode(png);
        Assert.Equal(bitmap.Width, bitmap.Height);
        Assert.InRange(bitmap.Width, 300, 700);   // about 45 modules of 8 pixels and the quiet zone
    }

    [Fact]
    public void ThePicture_IsDarkOnWhite_WithAWhiteQuietZone()
    {
        byte[] png = QrPayload.Png("https://t.me/proxy?server=1.2.3.4&port=1&secret=dd" + Secret);

        using SKBitmap bitmap = SKBitmap.Decode(png);
        Assert.Equal(SKColors.White, bitmap.GetPixel(0, 0));   // the corner is the quiet zone
        bool anyDark = false;
        for (int y = 0; y < bitmap.Height && !anyDark; y++)
            for (int x = 0; x < bitmap.Width; x++)
                if (bitmap.GetPixel(x, y).Red < 60) { anyDark = true; break; }
        Assert.True(anyDark);
    }

    [Theory]
    [InlineData("192.168.18.20", 1443)]
    [InlineData("10.255.255.255", 65535)]
    [InlineData("172.16.0.1", 1)]
    [InlineData("192.168.100.200", 18443)]
    public void ScanningThePicture_GivesBackExactlyTheLink(string ip, int port)
    {
        string link = QrPayload.Build(IPAddress.Parse(ip), port, Secret);

        string? scanned = Scan(QrPayload.Png(link));

        Assert.Equal(link, scanned);
    }

    [Fact]
    public void TheScanner_FindsNothingInAPictureWithNoCode()
    {
        // A negative control: were the scanner to "read" anything out of nothing, the round trips above would prove nothing.
        using var blank = new SKBitmap(300, 300);
        blank.Erase(SKColors.White);
        using SKImage image = SKImage.FromBitmap(blank);
        using SKData encoded = image.Encode(SKEncodedImageFormat.Png, 100);

        Assert.Null(Scan(encoded.ToArray()));
    }

    [Fact]
    public void ScanningThePicture_WorksWithAnotherSecretToo()
    {
        string link = QrPayload.Build(IPAddress.Parse("192.168.1.2"), 1443, "ffeeddccbbaa99887766554433221100");

        Assert.Equal(link, Scan(QrPayload.Png(link)));
    }

    [Fact]
    public void TheBiggerThePixels_TheBiggerThePicture_AndStillTheSameCode()
    {
        string link = QrPayload.Build(IPAddress.Parse("192.168.18.20"), 1443, Secret);

        byte[] small = QrPayload.Png(link, 4);
        byte[] large = QrPayload.Png(link, 12);

        using SKBitmap a = SKBitmap.Decode(small);
        using SKBitmap b = SKBitmap.Decode(large);
        Assert.Equal(3, b.Width / a.Width);
        Assert.Equal(link, Scan(large));
    }

    // ----- the view-model -----

    private static FakeNetInfo Net(params (string Name, string Ip)[] nics)
    {
        var net = new FakeNetInfo();
        net.SetInterfaces([.. nics.Select(n => new NetInterfaceInfo(n.Name, "Network adapter", true, NetKind.Ethernet,
            [IPAddress.Parse(n.Ip)], [IPAddress.Parse(n.Ip[..n.Ip.LastIndexOf('.')] + ".1")]))]);
        return net;
    }

    [Fact]
    public void TheCode_IsMadeFromTheSavedPortAndSecret_AtTheSavedAddress()
    {
        var settings = new FakeAppSettings { LanAddress = "10.1.1.9" };
        settings.Settings = settings.Settings with { Port = 1444 };
        settings.CurrentSecret = Secret;

        var qr = new QrViewModel(settings, Net(("enp5s0", "192.168.18.20"), ("wlp2s0", "10.1.1.9")), new FakeClipboard());

        Assert.True(qr.HasCode);
        Assert.False(qr.HasNoCode);
        Assert.Equal("https://t.me/proxy?server=10.1.1.9&port=1444&secret=dd" + Secret, qr.Link);
        Assert.Equal("10.1.1.9 (wlp2s0)", qr.AddressText);
        Assert.Equal(qr.Link, Scan(qr.Png!));
    }

    [Fact]
    public void WithoutAUsableSavedAddress_TheFirstOneIsUsed()
    {
        var settings = new FakeAppSettings { LanAddress = "172.30.0.9" };   // not on this machine any more

        var qr = new QrViewModel(settings, Net(("enp5s0", "192.168.18.20")), new FakeClipboard());

        Assert.Contains("server=192.168.18.20&", qr.Link);
    }

    [Fact]
    public void WithNoAddressAtAll_ThereIsNoCode_Only_AnExplanation()
    {
        var qr = new QrViewModel(new FakeAppSettings(), Net(), new FakeClipboard());

        Assert.False(qr.HasCode);
        Assert.True(qr.HasNoCode);
        Assert.Null(qr.Link);
        Assert.Null(qr.Png);
        Assert.Null(qr.AddressText);
        Assert.Contains("Wi-Fi", QrViewModel.NoAddressText);
    }

    [Fact]
    public async Task Copy_PutsTheLinkOnTheClipboard()
    {
        var clipboard = new FakeClipboard();
        var qr = new QrViewModel(new FakeAppSettings(), Net(("enp5s0", "192.168.18.20")), clipboard);

        await qr.CopyCommand.ExecuteAsync(null);

        Assert.Equal(qr.Link, clipboard.Text);
        Assert.Equal("Ссылка скопирована", qr.Notice);
    }

    [Fact]
    public async Task Copy_WithNoCode_DoesNothing()
    {
        var clipboard = new FakeClipboard();
        var qr = new QrViewModel(new FakeAppSettings(), Net(), clipboard);

        await qr.CopyCommand.ExecuteAsync(null);

        Assert.Null(clipboard.Text);
    }

    [Fact]
    public void TheNote_SaysWhoCanUseTheProxy_AndWhyThePhoneMightNotConnect()
    {
        Assert.Equal("Любой в этой сети, у кого есть код, сможет пользоваться прокси. Если телефон не подключается — брандмауэр мог заблокировать TgwsOne.",
                     QrViewModel.Note);
    }

    // ----- where it is opened from -----

    [Fact]
    public async Task HomeMenu_WithSharingOff_OpensTheSetting_NotTheCode()
    {
        await using var rig = new HomeRig();
        rig.Settings.LanSharing = false;

        await rig.Home.ShowQrCommand.ExecuteAsync(null);

        Assert.Equal([(AppPage.Settings, false)], rig.Navigator.Shown);
        Assert.Empty(rig.Dialogs.QrsShown);
    }

    [Fact]
    public async Task HomeMenu_WithSharingOn_ShowsTheCode()
    {
        await using var rig = new HomeRig();
        rig.Settings.LanSharing = true;
        rig.Net.SetInterfaces(new NetInterfaceInfo("enp5s0", "Network adapter", true, NetKind.Ethernet,
            [IPAddress.Parse("192.168.18.20")], [IPAddress.Parse("192.168.18.1")]));

        await rig.Home.ShowQrCommand.ExecuteAsync(null);

        QrViewModel shown = Assert.Single(rig.Dialogs.QrsShown);
        Assert.Contains("server=192.168.18.20&", shown.Link);
        Assert.Empty(rig.Navigator.Shown);
    }

    [Fact]
    public async Task SettingsButton_WithSharingOnlyTicked_AsksToSaveFirst()
    {
        var settings = new FakeAppSettings { LanSharing = false };
        var dialogs = new FakeDialogs();
        var net = Net(("enp5s0", "192.168.18.20"));
        var vm = new SettingsViewModel(settings, new FakeProxyControl(), dialogs, new FakeClipboard(), net, new FakeAutostart(),
                                       () => Task.CompletedTask, () => new QrViewModel(settings, net, new FakeClipboard()));
        vm.LanSharing = true;   // ticked, not saved

        await vm.ShowQrCommand.ExecuteAsync(null);

        Assert.Equal("Сначала сохраните настройки: раздача в сеть ещё не включена.", vm.Notice);
        Assert.Empty(dialogs.QrsShown);
    }

    [Fact]
    public async Task SettingsButton_WithSharingSaved_ShowsTheCode()
    {
        var settings = new FakeAppSettings { LanSharing = true };
        var dialogs = new FakeDialogs();
        var net = Net(("enp5s0", "192.168.18.20"));
        var vm = new SettingsViewModel(settings, new FakeProxyControl(), dialogs, new FakeClipboard(), net, new FakeAutostart(),
                                       () => Task.CompletedTask, () => new QrViewModel(settings, net, new FakeClipboard()));

        await vm.ShowQrCommand.ExecuteAsync(null);

        Assert.Single(dialogs.QrsShown);
    }

    // ----- the window -----

    private static (QrDialog Dialog, QrViewModel Vm) Show(FakeAppSettings settings, FakeNetInfo net)
    {
        var vm = new QrViewModel(settings, net, new FakeClipboard());
        var dialog = new QrDialog { DataContext = vm };
        dialog.Show();
        Dispatcher.UIThread.RunJobs();
        return (dialog, vm);
    }

    private static T Find<T>(QrDialog d, string name) where T : Control
        => d.FindControl<T>(name) ?? throw new InvalidOperationException($"no control named {name}");

    [AvaloniaFact]
    public void TheWindow_ShowsThePicture_TheAddress_TheLink_AndTheNote()
    {
        (QrDialog dialog, QrViewModel vm) = Show(new FakeAppSettings(), Net(("enp5s0", "192.168.18.20")));

        Assert.True(Find<StackPanel>(dialog, "CodePanel").IsVisible);
        Assert.False(Find<TextBlock>(dialog, "NoCodeText").IsVisible);
        Assert.NotNull(Find<Image>(dialog, "QrImage").Source);
        Assert.Equal("Адрес: 192.168.18.20 (enp5s0)", Find<TextBlock>(dialog, "AddressText").Text);
        Assert.Equal(vm.Link, Find<SelectableTextBlock>(dialog, "LinkText").Text);
        Assert.Equal(QrViewModel.Note, Find<TextBlock>(dialog, "NoteText").Text);
        Assert.Equal("Копировать", Find<Button>(dialog, "CopyButton").Content);
    }

    [AvaloniaFact]
    public void TheWindow_WithNoAddress_ExplainsInsteadOfShowingAPicture()
    {
        (QrDialog dialog, _) = Show(new FakeAppSettings(), Net());

        Assert.False(Find<StackPanel>(dialog, "CodePanel").IsVisible);
        Assert.True(Find<TextBlock>(dialog, "NoCodeText").IsVisible);
        Assert.Equal(QrViewModel.NoAddressText, Find<TextBlock>(dialog, "NoCodeText").Text);
        Assert.True(Find<TextBlock>(dialog, "NoteText").IsVisible);   // the note about who can use the proxy still applies
    }

    [AvaloniaFact]
    public void ThePicture_IsOnAWhiteCard_InTheDarkTheme_Too()
    {
        (QrDialog dialog, _) = Show(new FakeAppSettings(), Net(("enp5s0", "192.168.18.20")));
        dialog.RequestedThemeVariant = Avalonia.Styling.ThemeVariant.Dark;
        Dispatcher.UIThread.RunJobs();

        var card = Find<Image>(dialog, "QrImage").GetVisualAncestors().OfType<Border>().First();

        Assert.Equal(Avalonia.Media.Colors.White, ((Avalonia.Media.ISolidColorBrush)card.Background!).Color);
    }

    [AvaloniaFact]
    public void ThePngConverter_MakesAPictureOfThePng_AndNothingOfNothing()
    {
        byte[] png = QrPayload.Png("https://t.me/proxy?server=1.2.3.4&port=1&secret=dd" + Secret);

        Bitmap? bitmap = QrConverters.PngToBitmap.Convert(png, typeof(Bitmap), null, System.Globalization.CultureInfo.InvariantCulture) as Bitmap;

        Assert.NotNull(bitmap);
        Assert.Null(QrConverters.PngToBitmap.Convert(null, typeof(Bitmap), null, System.Globalization.CultureInfo.InvariantCulture));
    }
}
