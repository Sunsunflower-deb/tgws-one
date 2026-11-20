using System.Diagnostics;
using SkiaSharp;

namespace TgwsOne.Tests;

/// <summary>The Linux menu entry and its icon: valid, what the spec says, and installed (and removed) where desktops look for them.</summary>
public class DesktopEntryTests
{
    private static string RepoRoot()
    {
        for (string? dir = AppContext.BaseDirectory; dir != null; dir = Path.GetDirectoryName(dir))
            if (File.Exists(Path.Combine(dir, "Makefile")) && Directory.Exists(Path.Combine(dir, "packaging", "linux"))) return dir;
        throw new InvalidOperationException("the repository root was not found above " + AppContext.BaseDirectory);
    }

    private static string Entry => Path.Combine(RepoRoot(), "packaging", "linux", "tgwsone.desktop");
    private static string Icon => Path.Combine(RepoRoot(), "packaging", "linux", "tgwsone.png");

    private static string? Find(string program) => PrivateBus.Find(program);

    private static (int Exit, string Output) Run(string program, string workDir, params string[] args)
    {
        ProcessStartInfo info = PrivateBus.Start(program, args);
        info.WorkingDirectory = workDir;
        using Process process = Process.Start(info)!;
        string output = process.StandardOutput.ReadToEnd() + process.StandardError.ReadToEnd();
        process.WaitForExit();
        return (process.ExitCode, output);
    }

    /// <summary>The keys of the [Desktop Entry] group as written (localized ones included, by their full names).</summary>
    private static Dictionary<string, string> Keys()
        => File.ReadAllLines(Entry)
               .Where(l => l.Contains('=') && !l.StartsWith('#'))
               .Select(l => l.Split('=', 2))
               .ToDictionary(p => p[0], p => p[1]);

    [Fact]
    public void TheDesktopEntry_SaysWhatTheSpecSays()
    {
        Dictionary<string, string> keys = Keys();

        Assert.Equal("Application", keys["Type"]);
        Assert.Equal("TgwsOne", keys["Name"]);
        Assert.Equal("tgwsone", keys["Exec"]);
        Assert.Equal("tgwsone", keys["Icon"]);
        Assert.Equal("false", keys["Terminal"]);
        Assert.Contains("Network", keys["Categories"].Split(';'));
    }

    [Fact]
    public void TheDesktopEntry_IsValid()
    {
        string? validator = Find("desktop-file-validate");
        if (validator == null) Assert.Skip("desktop-file-validate is not installed");

        (int exit, string output) = Run(validator, RepoRoot(), Entry);

        Assert.True(exit == 0, output);
        Assert.Equal("", output.Trim());
    }

    [Fact]
    public void TheIcon_Is256Pixels_AndTheSamePictureAsTheLargestOneInTheAppIcon()
    {
        using SKBitmap icon = SKBitmap.Decode(Icon);
        Assert.Equal(256, icon.Width);
        Assert.Equal(256, icon.Height);

        // src/app.ico: ICONDIR (6 bytes), then 16-byte entries; a width of 0 means 256. The pictures inside are PNGs.
        byte[] ico = File.ReadAllBytes(Path.Combine(RepoRoot(), "src", "app.ico"));
        int count = BitConverter.ToUInt16(ico, 4);
        int entry = Enumerable.Range(0, count).Select(i => 6 + 16 * i).Single(o => ico[o] == 0 && ico[o + 1] == 0);
        int size = BitConverter.ToInt32(ico, entry + 8);
        int offset = BitConverter.ToInt32(ico, entry + 12);
        using SKBitmap fromIco = SKBitmap.Decode(ico.AsSpan(offset, size));

        Assert.Equal(256, fromIco.Width);
        int different = 0;
        for (int y = 0; y < 256; y++)
            for (int x = 0; x < 256; x++)
                if (icon.GetPixel(x, y) != fromIco.GetPixel(x, y)) different++;
        Assert.Equal(0, different);
    }

    [Fact]
    public void MakeInstall_PutsTheEntryAndTheIconWhereTheDesktopLooks_AndUninstallTakesThemAway()
    {
        string? make = Find("make");
        if (make == null) Assert.Skip("make is not installed");
        using var tmp = new TempDir();

        (int exit, string output) = Run(make, RepoRoot(), "install-desktop", $"DESTDIR={tmp.Path}", "PREFIX=/usr");
        Assert.True(exit == 0, output);

        string entry = Path.Combine(tmp.Path, "usr", "share", "applications", "tgwsone.desktop");
        string icon = Path.Combine(tmp.Path, "usr", "share", "icons", "hicolor", "256x256", "apps", "tgwsone.png");
        Assert.Equal(File.ReadAllBytes(Entry), File.ReadAllBytes(entry));
        Assert.Equal(File.ReadAllBytes(Icon), File.ReadAllBytes(icon));
        if (!OperatingSystem.IsWindows())
        {
            const UnixFileMode readable = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead | UnixFileMode.OtherRead;
            Assert.Equal(readable, File.GetUnixFileMode(entry));
            Assert.Equal(readable, File.GetUnixFileMode(icon));
        }

        (exit, output) = Run(make, RepoRoot(), "uninstall-desktop", $"DESTDIR={tmp.Path}", "PREFIX=/usr");
        Assert.True(exit == 0, output);
        Assert.Empty(Directory.EnumerateFiles(tmp.Path, "*", SearchOption.AllDirectories));
        Assert.False(Directory.Exists(Path.Combine(tmp.Path, "usr", "share", "icons", "hicolor", "256x256")));   // the directories it made go too
    }

    [Fact]
    public void Uninstall_LeavesAnotherPackagesIconsAlone()
    {
        string? make = Find("make");
        if (make == null) Assert.Skip("make is not installed");
        using var tmp = new TempDir();
        string other = Path.Combine(tmp.Path, "usr", "share", "icons", "hicolor", "256x256", "apps", "other.png");
        Directory.CreateDirectory(Path.GetDirectoryName(other)!);
        File.WriteAllText(other, "someone else's");
        Assert.Equal(0, Run(make, RepoRoot(), "install-desktop", $"DESTDIR={tmp.Path}", "PREFIX=/usr").Exit);

        Assert.Equal(0, Run(make, RepoRoot(), "uninstall-desktop", $"DESTDIR={tmp.Path}", "PREFIX=/usr").Exit);

        Assert.Equal("someone else's", File.ReadAllText(other));
    }
}
