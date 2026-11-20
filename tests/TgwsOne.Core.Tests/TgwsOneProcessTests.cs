namespace TgwsOne.Core.Tests;

/// <summary>The guard that keeps a test run from putting the app's window on the desktop of whoever runs it.</summary>
[Collection(EnvCollection.Name)]
public class TgwsOneProcessTests
{
    [Fact]
    public void NoArguments_IsRefused_BecauseThatStartsTheWindow()
    {
        Assert.Throws<ArgumentException>(() => TgwsOneProcess.StartInfo());
    }

    [Fact]
    public void TheChild_NeverInheritsADisplay()
    {
        string? display = Environment.GetEnvironmentVariable("DISPLAY");
        string? wayland = Environment.GetEnvironmentVariable("WAYLAND_DISPLAY");
        Environment.SetEnvironmentVariable("DISPLAY", ":0");
        Environment.SetEnvironmentVariable("WAYLAND_DISPLAY", "wayland-0");
        try
        {
            var psi = TgwsOneProcess.StartInfo("--help");

            Assert.False(psi.Environment.ContainsKey("DISPLAY"));
            Assert.False(psi.Environment.ContainsKey("WAYLAND_DISPLAY"));
            Assert.Equal("--help", psi.ArgumentList[^1]);
        }
        finally
        {
            Environment.SetEnvironmentVariable("DISPLAY", display);
            Environment.SetEnvironmentVariable("WAYLAND_DISPLAY", wayland);
        }
    }

    [Fact]
    public void TheChild_NeverInheritsTheSessionBus()
    {
        string? bus = Environment.GetEnvironmentVariable("DBUS_SESSION_BUS_ADDRESS");
        Environment.SetEnvironmentVariable("DBUS_SESSION_BUS_ADDRESS", "unix:path=/run/user/1000/bus");
        try
        {
            Assert.False(TgwsOneProcess.StartInfo("--help").Environment.ContainsKey("DBUS_SESSION_BUS_ADDRESS"));
        }
        finally
        {
            Environment.SetEnvironmentVariable("DBUS_SESSION_BUS_ADDRESS", bus);
        }
    }

    [Fact]
    public void TheChild_WritesOnlyToThrowawayDirectories_UnlessTheTestSaysOtherwise()
    {
        var psi = TgwsOneProcess.StartInfo("--help");
        string home = Environment.GetEnvironmentVariable("HOME") ?? "/nonexistent-home";

        foreach (string variable in new[]
                 {
                     "TGWS_DATA_DIR", "TGWS_SERVICE_ROOT", "XDG_CONFIG_HOME", "XDG_STATE_HOME", "XDG_DATA_HOME", "XDG_CACHE_HOME", "XDG_RUNTIME_DIR",
                 })
        {
            string? value = psi.Environment[variable];
            Assert.False(string.IsNullOrEmpty(value), variable + " is not set");
            Assert.StartsWith(TgwsOneProcess.Sandbox, value);
            Assert.DoesNotContain(home + "/.", value);
        }

        psi.Environment["TGWS_DATA_DIR"] = "/somewhere/else";   // the test's own directory wins
        Assert.Equal("/somewhere/else", psi.Environment["TGWS_DATA_DIR"]);
    }

    [Fact]
    public async Task ABareSelftest_CanOnlyWriteItsLogInsideTheSandbox()
    {
        // The leak this guards against: a --selftest started with no directory of its own wrote selftest.log into the real profile.
        var psi = TgwsOneProcess.StartInfo("--selftest");

        using System.Diagnostics.Process p = System.Diagnostics.Process.Start(psi)!;
        Task<string> error = p.StandardError.ReadToEndAsync();
        await p.StandardOutput.ReadToEndAsync();
        using var limit = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await p.WaitForExitAsync(limit.Token);
        await error;

        Assert.True(File.Exists(Path.Combine(TgwsOneProcess.Sandbox, "data", "logs", "selftest.log")), "the log is not where the sandbox says");
    }
}
