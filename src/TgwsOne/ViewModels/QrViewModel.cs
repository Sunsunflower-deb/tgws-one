using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using TgwsOne.Core;
using TgwsOne.Services;

namespace TgwsOne.ViewModels;

/// <summary>«QR для телефона»: the code for the saved port and secret at the address of this machine on the local network, and the same link as text.</summary>
internal sealed partial class QrViewModel : ObservableObject
{
    public const string Note = "Любой в этой сети, у кого есть код, сможет пользоваться прокси. Если телефон не подключается — брандмауэр мог заблокировать tgws-engine.";
    public const string NoAddressText = "Не нашёл адрес в этой сети: подключитесь к Wi-Fi или кабелю — тогда здесь появится код.";

    private readonly IClipboardService _clipboard;

    [ObservableProperty] private string? _notice;

    public QrViewModel(IAppSettings settings, INetInfo net, IClipboardService clipboard)
    {
        _clipboard = clipboard;

        // The address saved with the settings while it is still one of this machine's, otherwise the first that is.
        IReadOnlyList<LanAddress> candidates = LanShare.Candidates(net);
        LanAddress? address = candidates.FirstOrDefault(a => a.Address.ToString() == settings.LanAddress) ?? candidates.FirstOrDefault();
        if (address is null) return;

        ProxySettings s = settings.Load();
        Address = address;
        Link = QrPayload.Build(address.Address, s.Port, settings.Secret());
        Png = QrPayload.Png(Link);
    }

    /// <summary>The address the code points at, or null when this machine has none a phone could use.</summary>
    public LanAddress? Address { get; }

    public string? Link { get; }

    /// <summary>The picture, as a PNG.</summary>
    public byte[]? Png { get; }

    public bool HasCode => Link != null;

    public bool HasNoCode => Link == null;

    public string? AddressText => Address?.ToString();

    [RelayCommand]
    private async Task CopyAsync()
    {
        if (Link is null) return;
        await _clipboard.SetTextAsync(Link);
        Notice = "Ссылка скопирована";
    }
}
