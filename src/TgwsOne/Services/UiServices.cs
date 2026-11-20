using Avalonia.Controls;
using Avalonia.Input.Platform;
using Avalonia.Threading;
using TgwsOne.ViewModels;
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

    public async Task ShowFinderAsync(FinderViewModel finder)
    {
        if (owner() is not { } window) return;
        await new FinderDialog { DataContext = finder }.ShowDialog(window);
    }
}

/// <summary>The real <see cref="ITicker"/>: a timer of the UI thread, so a tick can touch the screen's data without marshalling.</summary>
internal sealed class DispatcherTicker : ITicker
{
    public IDisposable Start(TimeSpan period, Action tick)
    {
        var timer = new DispatcherTimer(period, DispatcherPriority.Background, (_, _) => tick());
        timer.Start();
        return new Stop(timer);
    }

    private sealed class Stop(DispatcherTimer timer) : IDisposable
    {
        public void Dispose() => timer.Stop();
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
