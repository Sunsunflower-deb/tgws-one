using System.ComponentModel;
using System.Diagnostics;

namespace TgwsOne.Services;

/// <summary>The real <see cref="ILauncher"/>: the shell on Windows, <c>xdg-open</c> on Linux.</summary>
internal sealed class ShellLauncher : ILauncher
{
    private static readonly TimeSpan HandlerGrace = TimeSpan.FromSeconds(5);

    public async Task<bool> OpenAsync(string url)
    {
        try
        {
            if (OperatingSystem.IsWindows())
            {
                using Process? shell = Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
                return true;   // ShellExecute reports a missing handler itself, as an exception
            }

            using Process? opener = Process.Start(new ProcessStartInfo("xdg-open") { ArgumentList = { url }, UseShellExecute = false });
            if (opener is null) return false;

            // xdg-open hands the URL over and exits; a non-zero code means no handler. A handler that stays attached (Telegram
            // started from here) is as good as an exit: it is running, and that is all the user needs.
            using var grace = new CancellationTokenSource(HandlerGrace);
            try
            {
                await opener.WaitForExitAsync(grace.Token);
            }
            catch (OperationCanceledException)
            {
                return true;
            }
            return opener.ExitCode == 0;
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException)
        {
            return false;   // no xdg-open, no handler
        }
    }
}
