using System.Runtime.InteropServices;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Threading;
using TgwsOne.Core;

namespace TgwsOne.Services;

/// <summary>
/// How the application's window and process relate: the × hides (or minimizes) the window instead of ending the program,
/// «Выход» — from the sidebar, Ctrl+Q or the tray — stops the proxy and then ends it, and so does the session closing
/// (a logout, a SIGTERM). The proxy is stopped first on every way out: a proxy that outlived the application would be one nobody
/// can see or switch off.
/// </summary>
internal sealed class AppShell : IDisposable
{
    private readonly IClassicDesktopStyleApplicationLifetime _desktop;
    private readonly Window _window;
    private readonly AppServices _services;
    private readonly CloseBehavior _close;
    private readonly List<PosixSignalRegistration> _signals = [];
    private bool _quitting;
    private bool _proxyStopped;

    public AppShell(IClassicDesktopStyleApplicationLifetime desktop, Window window, AppServices services, CloseBehavior close)
    {
        _desktop = desktop;
        _window = window;
        _services = services;
        _close = close;

        window.Closing += OnClosing;
        desktop.ShutdownRequested += OnShutdownRequested;
        if (!OperatingSystem.IsWindows()) RegisterSignals();
    }

    /// <summary>Brings the window up wherever it is: hidden to the tray, minimized, behind others.</summary>
    public void Show()
    {
        if (!_window.IsVisible) _window.Show();
        if (_window.WindowState == WindowState.Minimized) _window.WindowState = WindowState.Normal;
        _window.Activate();
    }

    /// <summary>Stops the proxy, then ends the application. Safe to call more than once.</summary>
    public void Quit() => _ = QuitAsync();

    private async Task QuitAsync()
    {
        if (_quitting) return;
        _quitting = true;
        _close.Quitting = true;
        try
        {
            await _services.Proxy.StopAsync().ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            // Quitting has to finish whatever the stop says: a program that cannot be closed is worse than a proxy left running.
            _services.Log.Write($"остановка прокси при выходе не удалась: {ex.GetType().Name}: {ex.Message}");
        }
        _proxyStopped = true;
        _desktop.Shutdown();
    }

    private void OnClosing(object? sender, WindowClosingEventArgs e)
    {
        bool byUser = e.CloseReason == WindowCloseReason.WindowClosing && !e.IsProgrammatic;
        switch (_close.OnClosing(byUser))
        {
            case CloseAction.HideToTray:
                e.Cancel = true;
                _window.Hide();
                break;
            case CloseAction.Minimize:
                e.Cancel = true;
                _window.WindowState = WindowState.Minimized;
                break;
        }
    }

    // The session is ending (or something asked the application to stop): the proxy goes first, then the application follows.
    private void OnShutdownRequested(object? sender, ShutdownRequestedEventArgs e)
    {
        if (_proxyStopped) return;
        e.Cancel = true;
        Quit();
    }

    private void RegisterSignals()
    {
        foreach (PosixSignal signal in new[] { PosixSignal.SIGTERM, PosixSignal.SIGHUP, PosixSignal.SIGINT })
        {
            try
            {
                _signals.Add(PosixSignalRegistration.Create(signal, context =>
                {
                    context.Cancel = true;
                    Dispatcher.UIThread.Post(Quit);
                }));
            }
            catch (PlatformNotSupportedException)
            {
                // a signal this platform does not have
            }
        }
    }

    public void Dispose()
    {
        foreach (PosixSignalRegistration signal in _signals) signal.Dispose();
        _signals.Clear();
    }
}
