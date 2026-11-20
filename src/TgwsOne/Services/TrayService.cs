using Avalonia;
using Avalonia.Controls;
using Avalonia.Media.Imaging;
using TgwsOne.ViewModels;

namespace TgwsOne.Services;

/// <summary>
/// The tray icon of the platform (a status-notifier item on Linux, the notification area on Windows) showing what
/// <see cref="TrayViewModel"/> says: the icon with its status dot, the tooltip, the menu. Only made where there is a tray
/// (<see cref="TrayAvailability"/>). Everything runs on the UI thread, where the view-model's changes arrive.
/// </summary>
internal sealed class TrayService : IDisposable
{
    private readonly TrayViewModel _vm;
    private readonly Func<Stream> _baseIcon;
    private readonly TrayIcon _icon = new();
    private readonly Dictionary<Tone, WindowIcon> _icons = [];
    private string? _signature;
    private string? _menuSignature;

    public TrayService(Application app, TrayViewModel vm, Func<Stream> baseIcon, Action show)
    {
        _vm = vm;
        _baseIcon = baseIcon;
        _icon.Clicked += (_, _) => show();
        TrayIcon.SetIcons(app, [_icon]);
        vm.Changed += Refresh;
        Refresh();
    }

    private void Refresh()
    {
        string signature = _vm.Signature;
        if (signature == _signature) return;   // nothing that shows has changed: the platform's menu is left alone (an open one would close)
        _signature = signature;

        _icon.ToolTipText = _vm.Tooltip;
        _icon.Icon = IconFor(_vm.Tone);

        IReadOnlyList<TrayItem> items = _vm.Items;
        string menu = string.Join('/', items.Select(i => i.Signature));
        if (menu != _menuSignature)
        {
            _menuSignature = menu;
            _icon.Menu = BuildMenu(items);
        }
        _icon.IsVisible = true;
    }

    private WindowIcon IconFor(Tone tone)
    {
        if (_icons.TryGetValue(tone, out WindowIcon? icon)) return icon;
        using Stream source = _baseIcon();
        byte[] png = TrayIconImage.RenderPng(source, tone);
        return _icons[tone] = new WindowIcon(new Bitmap(new MemoryStream(png)));
    }

    private static NativeMenu BuildMenu(IReadOnlyList<TrayItem> items)
    {
        var menu = new NativeMenu();
        foreach (TrayItem item in items)
        {
            if (item.IsSeparator)
            {
                menu.Add(new NativeMenuItemSeparator());
                continue;
            }

            var native = new NativeMenuItem(item.Text) { Command = item.Command, IsEnabled = item.IsEnabled };
            if (item.IsRadio)
            {
                native.ToggleType = MenuItemToggleType.Radio;
                native.IsChecked = item.IsChecked;
            }
            if (item.Children != null) native.Menu = BuildMenu(item.Children);
            menu.Add(native);
        }
        return menu;
    }

    public void Dispose()
    {
        _vm.Changed -= Refresh;
        _icon.IsVisible = false;
        _icon.Dispose();
    }
}
