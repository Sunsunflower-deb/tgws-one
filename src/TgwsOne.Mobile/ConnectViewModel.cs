using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace TgwsOne.Mobile;

/// <summary>
/// Подключение к Telegram: the <c>tg://proxy</c> link, the button that hands it to Telegram and the button that
/// copies it for a manual paste. The link is read from the saved settings on every refresh.
/// </summary>
public sealed partial class ConnectViewModel : ObservableObject
{
    private readonly IMobileSettings _settings;
    private readonly ILauncher _launcher;
    private readonly IClipboardService _clipboard;
    private readonly IShareService _share;

    [ObservableProperty] private string _link = "";
    [ObservableProperty] private string? _notice;
    [ObservableProperty] private string? _errorText;

    public ConnectViewModel(IMobileSettings settings, ILauncher launcher, IClipboardService clipboard,
                            IShareService? share = null)
    {
        _settings = settings;
        _launcher = launcher;
        _clipboard = clipboard;
        _share = share ?? new NoShare();
        Refresh();
    }

    /// <summary>Reads the link again (the port or the secret may have changed).</summary>
    public void Refresh() => Link = _settings.TelegramLink();

    [RelayCommand]
    private async Task OpenInTelegramAsync()
    {
        Notice = null;
        ErrorText = null;
        if (await _launcher.OpenAsync(Link)) return;
        ErrorText = "Не удалось открыть Telegram. Скопируйте ссылку и вставьте её в Telegram вручную.";
    }

    [RelayCommand]
    private async Task CopyLinkAsync()
    {
        ErrorText = null;
        await _clipboard.SetTextAsync(Link);
        Notice = "Ссылка скопирована";
    }

    [RelayCommand]
    private async Task ShareLinkAsync()
    {
        ErrorText = null;
        await _share.ShareAsync(Link);
        Notice = "Ссылка отправлена";
    }

    /// <summary>Used by the view-model tests and by a build without a sharing surface: the button does nothing harmful.</summary>
    private sealed class NoShare : IShareService
    {
        public Task ShareAsync(string text) => Task.CompletedTask;
    }
}
