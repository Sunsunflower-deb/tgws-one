using System.Runtime.Versioning;

namespace TgwsOne.Core.Tests;

/// <summary>Our data, logs and run directories hold the secret: on Linux they must be 0700.</summary>
public class PrivateDirectoryTests
{
    private const UnixFileMode Private = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute;
    private const UnixFileMode Loose = Private | UnixFileMode.GroupRead | UnixFileMode.GroupExecute
                                       | UnixFileMode.OtherRead | UnixFileMode.OtherExecute;   // 0755

    [UnsupportedOSPlatform("windows")]
    private static UnixFileMode Mode(string path) => File.GetUnixFileMode(path);

    [UnsupportedOSPlatform("windows")]
    private static string MakeLoose(string path)
    {
        Directory.CreateDirectory(path);
        File.SetUnixFileMode(path, Loose);
        return path;
    }

    [Fact]
    public void Ensure_FreshDirectory_Is0700()
    {
        if (OperatingSystem.IsWindows()) return;
        using var tmp = new TempDir();
        string dir = tmp.Combine("data");

        PrivateDirectory.Ensure(dir);

        Assert.Equal(Private, Mode(dir));
    }

    [Fact]
    public void Ensure_ExistingLooseDirectory_IsTightenedTo0700()
    {
        if (OperatingSystem.IsWindows()) return;
        using var tmp = new TempDir();
        string dir = MakeLoose(tmp.Combine("data"));
        Assert.Equal(Loose, Mode(dir));

        PrivateDirectory.Ensure(dir);

        Assert.Equal(Private, Mode(dir));
    }

    [Fact]
    public void Ensure_NeverTouchesAnExistingParent()
    {
        if (OperatingSystem.IsWindows()) return;
        using var tmp = new TempDir();
        string parent = MakeLoose(tmp.Combine("parent"));

        PrivateDirectory.Ensure(Path.Combine(parent, "tgwsone"));
        PrivateDirectory.Ensure(Path.Combine(parent, "tgwsone"));   // and again, now that it exists

        Assert.Equal(Loose, Mode(parent));
        Assert.Equal(Private, Mode(Path.Combine(parent, "tgwsone")));
    }

    [Fact]
    public void Ensure_ExistingFilesInsideAreNotChanged()
    {
        if (OperatingSystem.IsWindows()) return;
        using var tmp = new TempDir();
        string dir = MakeLoose(tmp.Combine("data"));
        string file = Path.Combine(dir, "keep.txt");
        File.WriteAllText(file, "x");
        UnixFileMode before = Mode(file);

        PrivateDirectory.Ensure(dir);

        Assert.Equal(before, Mode(file));
    }

    [Fact]
    public void Ensure_IsIdempotent_AndHandlesAnAlreadyPrivateDirectory()
    {
        using var tmp = new TempDir();
        string dir = tmp.Combine("data");

        PrivateDirectory.Ensure(dir);
        PrivateDirectory.Ensure(dir);

        Assert.True(Directory.Exists(dir));
        if (!OperatingSystem.IsWindows()) Assert.Equal(Private, Mode(dir));
    }

    // ---------- every writer that creates one of our directories goes through it ----------

    [Fact]
    public void IniFile_Save_CreatesAndTightensItsDirectory()
    {
        if (OperatingSystem.IsWindows()) return;
        using var tmp = new TempDir();

        var fresh = IniFile.Load(tmp.Combine("fresh", "a.ini"));
        fresh.Set("app", "secret", "x");
        fresh.Save();
        Assert.Equal(Private, Mode(tmp.Combine("fresh")));

        string existing = MakeLoose(tmp.Combine("existing"));
        var loose = IniFile.Load(Path.Combine(existing, "a.ini"));
        loose.Set("app", "secret", "x");
        loose.Save();
        Assert.Equal(Private, Mode(existing));
    }

    [Fact]
    public void AppLog_Write_CreatesAndTightensItsDirectory()
    {
        if (OperatingSystem.IsWindows()) return;
        using var tmp = new TempDir();

        new AppLog(tmp.Combine("fresh", "app.log")).Write("x");
        Assert.Equal(Private, Mode(tmp.Combine("fresh")));

        string existing = MakeLoose(tmp.Combine("existing"));
        new AppLog(Path.Combine(existing, "app.log")).Write("x");
        Assert.Equal(Private, Mode(existing));
    }

    [Fact]
    public void EngineRecord_Write_CreatesAndTightensTheRunDirectory()
    {
        if (OperatingSystem.IsWindows()) return;
        using var tmp = new TempDir();
        var rec = new EngineRecord(1, DateTime.UtcNow, "/x", "h");

        rec.Write(tmp.Combine("fresh", "engine.pid"));
        Assert.Equal(Private, Mode(tmp.Combine("fresh")));

        string existing = MakeLoose(tmp.Combine("existing"));
        rec.Write(Path.Combine(existing, "engine.pid"));
        Assert.Equal(Private, Mode(existing));
    }

    [Collection(EnvCollection.Name)]
    public class Launcher : IDisposable
    {
        private readonly TestEnv _env = new();

        public void Dispose() => _env.Dispose();

        [Fact]
        public void DetachedLauncher_Start_CreatesAndTightensTheLogDirectory()
        {
            if (OperatingSystem.IsWindows()) return;
            _env.Set("FAKE_MODE", "exit:0");
            string existing = MakeLoose(_env.Scratch("existing"));

            using var p1 = DetachedLauncher.Start(TestEnv.FakeEnginePath, [], _env.Scratch("fresh/console.log"), AppContext.BaseDirectory);
            using var p2 = DetachedLauncher.Start(TestEnv.FakeEnginePath, [], Path.Combine(existing, "console.log"), AppContext.BaseDirectory);
            p1.WaitForExit(10_000);
            p2.WaitForExit(10_000);

            Assert.Equal(Private, Mode(_env.Scratch("fresh")));
            Assert.Equal(Private, Mode(existing));
        }

        [Fact]
        public async Task StartingTheProxy_LeavesDataLogsAndRunPrivate()
        {
            if (OperatingSystem.IsWindows()) return;
            // Pre-existing, loose: the situation after an upgrade from a build that created 0755 directories.
            MakeLoose(_env.DataDir);
            MakeLoose(_env.Paths.LogsDir);
            MakeLoose(_env.Paths.RunDir);
            var ini = IniFile.Load(_env.Paths.Ini);
            ini.Set("proxy", "port", TestEnv.FreePort().ToString());
            ini.Save();
            var c = new ProxyController(_env.Paths, new AppLog(_env.Paths.AppLogFile), TimeSpan.FromSeconds(10));

            Assert.True((await c.StartAsync()).Ok);

            Assert.Equal(Private, Mode(_env.DataDir));
            Assert.Equal(Private, Mode(_env.Paths.LogsDir));
            Assert.Equal(Private, Mode(_env.Paths.RunDir));
        }

        [Fact]
        public async Task StartingTheProxy_FromScratch_CreatesPrivateDirectories()
        {
            if (OperatingSystem.IsWindows()) return;
            var c = new ProxyController(_env.Paths, new AppLog(_env.Paths.AppLogFile), TimeSpan.FromSeconds(10));
            var ini = IniFile.Load(_env.Paths.Ini);
            ini.Set("proxy", "port", TestEnv.FreePort().ToString());
            ini.Save();

            Assert.True((await c.StartAsync()).Ok);

            Assert.Equal(Private, Mode(_env.DataDir));
            Assert.Equal(Private, Mode(_env.Paths.LogsDir));
            Assert.Equal(Private, Mode(_env.Paths.RunDir));
        }
    }
}
