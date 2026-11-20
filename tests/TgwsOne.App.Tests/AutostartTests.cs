using System.Diagnostics;
using TgwsOne.Services;

namespace TgwsOne.Tests;

internal sealed class FakeRegistryRun : IRegistryRun
{
    public Dictionary<string, string> Values { get; } = [];

    public string? Get(string name) => Values.GetValueOrDefault(name);
    public void Set(string name, string value) => Values[name] = value;
    public void Remove(string name) => Values.Remove(name);
}

internal sealed class FakeAutostart : IAutostart
{
    public bool Enabled { get; set; }
    public Exception? Throws { get; set; }
    public List<bool> Calls { get; } = [];

    public bool IsEnabled() => Enabled;

    public void Set(bool on)
    {
        Calls.Add(on);
        if (Throws is { } ex) throw ex;
        Enabled = on;
    }
}

public class AutostartTests
{
    private const string Exe = "/usr/lib/tgwsone/tgwsone";

    // ----- Linux -----

    [Fact]
    public void TheDesktopEntry_IsExactlyThis()
    {
        Assert.Equal(
            "[Desktop Entry]\n" +
            "Type=Application\n" +
            "Name=TgwsOne\n" +
            "Comment=Прокси для Telegram\n" +
            "Exec=/usr/lib/tgwsone/tgwsone --tray\n" +
            "Icon=tgwsone\n" +
            "Terminal=false\n" +
            "X-GNOME-Autostart-enabled=true\n",
            LinuxAutostart.Text(Exe));
    }

    [Theory]
    [InlineData("/opt/Tgws One/tgwsone", "Exec=\"/opt/Tgws One/tgwsone\" --tray")]
    [InlineData("/home/user/my$app/tgwsone", "Exec=\"/home/user/my\\$app/tgwsone\" --tray")]
    [InlineData("/home/a\"b/tgwsone", "Exec=\"/home/a\\\"b/tgwsone\" --tray")]
    [InlineData("/usr/bin/tgwsone", "Exec=/usr/bin/tgwsone --tray")]
    public void ThePathIsQuotedByTheDesktopEntryRules(string exe, string expectedLine)
    {
        Assert.Contains(expectedLine, LinuxAutostart.Text(exe).Split('\n'));
    }

    [Fact]
    public void SettingItOn_WritesTheFile_CreatingTheFolder_AndSettingItOffRemovesIt()
    {
        using var tmp = new TempDir();
        var autostart = new LinuxAutostart(tmp.Combine("config"), Exe);
        string file = tmp.Combine("config", "autostart", "tgwsone.desktop");
        Assert.False(autostart.IsEnabled());

        autostart.Set(true);
        Assert.True(File.Exists(file));
        Assert.True(autostart.IsEnabled());
        Assert.Equal(LinuxAutostart.Text(Exe), File.ReadAllText(file));

        autostart.Set(false);
        Assert.False(File.Exists(file));
        Assert.False(autostart.IsEnabled());
    }

    [Fact]
    public void SettingItOffWhenItIsNotThere_IsFine()
    {
        using var tmp = new TempDir();
        var autostart = new LinuxAutostart(tmp.Combine("config"), Exe);

        autostart.Set(false);

        Assert.False(autostart.IsEnabled());
    }

    [Fact]
    public void SettingItOnTwice_IsTheSameAsOnce()
    {
        using var tmp = new TempDir();
        var autostart = new LinuxAutostart(tmp.Combine("config"), Exe);

        autostart.Set(true);
        autostart.Set(true);

        Assert.Equal(LinuxAutostart.Text(Exe), File.ReadAllText(tmp.Combine("config", "autostart", "tgwsone.desktop")));
    }

    [Theory]
    [InlineData("Hidden=true")]
    [InlineData("X-GNOME-Autostart-enabled=false")]
    public void AnEntryThatTheDesktopSwitchedOff_IsNotEnabled(string line)
    {
        using var tmp = new TempDir();
        string file = tmp.Combine("config", "autostart", "tgwsone.desktop");
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        File.WriteAllText(file, LinuxAutostart.Text(Exe) + line + "\n");

        Assert.False(new LinuxAutostart(tmp.Combine("config"), Exe).IsEnabled());
    }

    [Fact]
    public void TheGeneratedFile_PassesDesktopFileValidate()
    {
        string? tool = new[] { "/usr/bin/desktop-file-validate", "/usr/sbin/desktop-file-validate" }.FirstOrDefault(File.Exists);
        if (tool is null || !OperatingSystem.IsLinux()) return;   // the tool is not everywhere

        using var tmp = new TempDir();
        new LinuxAutostart(tmp.Combine("config"), Exe).Set(true);
        var psi = new ProcessStartInfo(tool) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        psi.ArgumentList.Add(tmp.Combine("config", "autostart", "tgwsone.desktop"));
        psi.Environment.Remove("DISPLAY");

        using Process p = Process.Start(psi)!;
        string output = p.StandardOutput.ReadToEnd() + p.StandardError.ReadToEnd();
        p.WaitForExit();

        Assert.True(p.ExitCode == 0, output);
    }

    // ----- Windows (through a fake registry; the real one is exercised on Windows in SP5) -----

    [Fact]
    public void Windows_WritesTheRunValue_WithTheTraySwitch()
    {
        var registry = new FakeRegistryRun();
        var autostart = new WindowsAutostart(registry, @"C:\Program Files\TgwsOne\TgwsOne.exe");
        Assert.False(autostart.IsEnabled());

        autostart.Set(true);

        Assert.Equal("\"C:\\Program Files\\TgwsOne\\TgwsOne.exe\" --tray", registry.Values["TgwsOne"]);
        Assert.True(autostart.IsEnabled());
    }

    [Fact]
    public void Windows_SettingItOff_RemovesTheValue()
    {
        var registry = new FakeRegistryRun();
        var autostart = new WindowsAutostart(registry, @"C:\TgwsOne.exe");
        autostart.Set(true);

        autostart.Set(false);

        Assert.Empty(registry.Values);
        Assert.False(autostart.IsEnabled());
        autostart.Set(false);   // and again: nothing there, nothing wrong
    }

    [Fact]
    public void TheCommandLine_QuotesTheExecutable()
    {
        Assert.Equal("\"C:\\a b\\x.exe\" --tray", Autostart.CommandLine(@"C:\a b\x.exe"));
    }

    [Fact]
    public void ForThisMachine_OnLinux_IsTheDesktopEntryWriter()
    {
        if (!OperatingSystem.IsLinux()) return;

        Assert.IsType<LinuxAutostart>(Autostart.ForThisMachine(Exe));
    }
}
