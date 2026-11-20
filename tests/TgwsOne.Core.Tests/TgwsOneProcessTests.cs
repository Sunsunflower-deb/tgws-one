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
}
