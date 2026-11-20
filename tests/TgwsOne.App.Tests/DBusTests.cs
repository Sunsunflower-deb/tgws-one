using System.Diagnostics;
using Tmds.DBus.Protocol;
using TgwsOne.Services;

namespace TgwsOne.Tests;

/// <summary>A session bus of its own: a private <c>dbus-daemon</c> with no service directories (nothing can be started on it) and no display.</summary>
internal sealed class PrivateBus : IDisposable
{
    private readonly Process _daemon;
    private readonly TempDir _dir;

    private PrivateBus(Process daemon, TempDir dir, string address)
    {
        _daemon = daemon;
        _dir = dir;
        Address = address;
    }

    public string Address { get; }

    public static string? Find(string program)
        => (Environment.GetEnvironmentVariable("PATH") ?? "").Split(':', StringSplitOptions.RemoveEmptyEntries)
            .Append("/usr/sbin").Append("/usr/bin")
            .Select(d => Path.Combine(d, program)).FirstOrDefault(File.Exists);

    public static ProcessStartInfo Start(string program, params string[] args)
    {
        var info = new ProcessStartInfo(program) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        foreach (string a in args) info.ArgumentList.Add(a);
        info.Environment.Remove("DISPLAY");
        info.Environment.Remove("WAYLAND_DISPLAY");
        return info;
    }

    /// <returns>Null when there is no <c>dbus-daemon</c> on this machine (the tests that need it are skipped).</returns>
    public static PrivateBus? TryStart()
    {
        string? exe = Find("dbus-daemon");
        if (exe == null) return null;

        var dir = new TempDir();
        string config = Path.Combine(dir.Path, "bus.conf");
        File.WriteAllText(config, $"""
            <!DOCTYPE busconfig PUBLIC "-//freedesktop//DTD D-Bus Bus Configuration 1.0//EN" "http://www.freedesktop.org/standards/dbus/1.0/busconfig.dtd">
            <busconfig>
              <type>session</type>
              <listen>unix:path={dir.Path}/bus</listen>
              <auth>EXTERNAL</auth>
              <policy context="default">
                <allow send_destination="*" eavesdrop="true"/>
                <allow eavesdrop="true"/>
                <allow own="*"/>
              </policy>
            </busconfig>
            """);
        Process daemon = Process.Start(Start(exe, $"--config-file={config}", "--nofork", "--print-address=1"))!;
        string? address = daemon.StandardOutput.ReadLine();
        if (string.IsNullOrWhiteSpace(address))
        {
            daemon.Kill();
            dir.Dispose();
            throw new InvalidOperationException("dbus-daemon printed no address: " + daemon.StandardError.ReadToEnd());
        }
        return new PrivateBus(daemon, dir, address.Trim());
    }

    public void Dispose()
    {
        try { _daemon.Kill(); }
        catch (InvalidOperationException) { /* already gone */ }
        _daemon.WaitForExit(3000);
        _daemon.Dispose();
        _dir.Dispose();
    }
}

/// <summary><c>dbus-monitor</c> on a private bus: what really went over the wire, as text.</summary>
internal sealed class BusMonitor : IDisposable
{
    private readonly Process _process;
    private readonly List<string> _lines = [];

    private BusMonitor(Process process)
    {
        _process = process;
        process.OutputDataReceived += (_, e) =>
        {
            if (e.Data != null) lock (_lines) _lines.Add(e.Data);
        };
        process.BeginOutputReadLine();
    }

    public static BusMonitor? TryStart(string address)
    {
        string? exe = PrivateBus.Find("dbus-monitor");
        return exe == null ? null : new BusMonitor(Process.Start(PrivateBus.Start(exe, "--address", address))!);
    }

    public IReadOnlyList<string> Lines { get { lock (_lines) return [.. _lines]; } }

    public void Dispose()
    {
        try { _process.Kill(); }
        catch (InvalidOperationException) { /* already gone */ }
        _process.WaitForExit(3000);
        _process.Dispose();
    }
}

/// <summary>The notification and tray-check code against a real bus: the wire format is what the desktop's daemon will be sent.</summary>
public class DBusTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        DateTime limit = DateTime.UtcNow.AddSeconds(10);
        while (!condition())
        {
            if (DateTime.UtcNow > limit) throw new TimeoutException("the condition never became true");
            await Task.Delay(20, Ct);
        }
    }

    /// <summary>Skips the test (it cannot run here) when the thing it needs is not on this machine.</summary>
    private static T Need<T>(T? value, string reason) where T : class
    {
        if (value == null) Assert.Skip(reason);
        return value!;
    }

    [Fact]
    public async Task Notify_PutsTheDocumentedCallOnTheBus_AndIsQuietWithoutADaemon()
    {
        Assert.SkipUnless(OperatingSystem.IsLinux(), "the Linux sink");
        using PrivateBus bus = Need(PrivateBus.TryStart(), "dbus-daemon is not installed");
        using BusMonitor monitor = Need(BusMonitor.TryStart(bus.Address), "dbus-monitor is not installed");
        await WaitUntilAsync(() => monitor.Lines.Count > 0);   // the monitor is on the bus

        // Nobody owns org.freedesktop.Notifications here: the call is answered with an error, and that is not ours to raise.
        await new LinuxNotificationSink(bus.Address).ShowAsync("Заголовок", "Текст «в кавычках»", Ct);

        await WaitUntilAsync(() => monitor.Lines.Any(l => l.Contains("member=Notify")));
        await WaitUntilAsync(() => monitor.Lines.Any(l => l.Trim() == "int32 -1"));
        string[] lines = [.. monitor.Lines.Select(l => l.Trim())];
        int call = Array.FindIndex(lines, l => l.Contains("member=Notify") && l.StartsWith("method call", StringComparison.Ordinal));
        Assert.True(call >= 0, "no Notify call was seen:\n" + string.Join('\n', lines));
        Assert.Contains("destination=org.freedesktop.Notifications", lines[call]);
        Assert.Contains("path=/org/freedesktop/Notifications", lines[call]);
        Assert.Contains("interface=org.freedesktop.Notifications", lines[call]);
        Assert.Equal(
            [
                "string \"TgwsOne\"",
                "uint32 0",
                "string \"tgwsone\"",
                "string \"Заголовок\"",
                "string \"Текст «в кавычках»\"",
                "array [",
                "]",
                "array [",
                "]",
                "int32 -1",
            ],
            lines.Skip(call + 1).Take(10));
    }

    [Fact]
    public async Task Notify_WithNoBusAtAll_IsQuiet()
    {
        Assert.SkipUnless(OperatingSystem.IsLinux(), "the Linux sink");

        await new LinuxNotificationSink("unix:path=/nonexistent/tgwsone-test-bus").ShowAsync("a", "b", Ct);
    }

    [Fact]
    public async Task TheTray_IsAvailable_OnlyWhenSomethingOwnsTheWatcherName()
    {
        Assert.SkipUnless(OperatingSystem.IsLinux(), "the Linux check");
        using PrivateBus bus = Need(PrivateBus.TryStart(), "dbus-daemon is not installed");

        Assert.False(await TrayAvailability.CheckAsync(bus.Address, Ct));

        using var watcher = new DBusConnection(bus.Address);
        await watcher.ConnectAsync();
        await watcher.RequestNameAsync(TrayAvailability.Watcher, RequestNameOptions.None);

        Assert.True(await TrayAvailability.CheckAsync(bus.Address, Ct));
    }

    [Fact]
    public async Task TheTray_IsNotAvailable_WithoutASessionBus()
    {
        Assert.SkipUnless(OperatingSystem.IsLinux(), "the Linux check");

        Assert.False(await TrayAvailability.CheckAsync("unix:path=/nonexistent/tgwsone-test-bus", Ct));
    }
}
