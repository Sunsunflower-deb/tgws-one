using Avalonia.Controls;
using Avalonia.Input.Platform;
using TgwsOne.Views;

namespace TgwsOne.Services;

/// <summary>The real <see cref="IDialogService"/>: modal windows over the main window.</summary>
internal sealed class AvaloniaDialogService(Func<Window?> owner) : IDialogService
{
    public async Task<bool> ConfirmAsync(string title, string message, string accept, string cancel)
    {
        if (owner() is not { } window) return false;
        var dialog = new ConfirmDialog(title, message, accept, cancel);
        return await dialog.ShowDialog<bool>(window);
    }
}

/// <summary>The real <see cref="IClipboardService"/>: the clipboard of the main window.</summary>
internal sealed class AvaloniaClipboardService(Func<Window?> owner) : IClipboardService
{
    public async Task SetTextAsync(string text)
    {
        if (owner()?.Clipboard is { } clipboard) await clipboard.SetTextAsync(text);
    }
}
