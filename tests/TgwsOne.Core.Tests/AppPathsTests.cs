namespace TgwsOne.Core.Tests;

public class AppPathsTests
{
    private static Func<string, string?> Env(params (string Key, string Value)[] vars)
    {
        var d = vars.ToDictionary(v => v.Key, v => v.Value);
        return k => d.TryGetValue(k, out var v) ? v : null;
    }

    [Fact]
    public void Windows_UsesLocalAppData()
    {
        var p = AppPaths.For(OsKind.Windows, Env(("LOCALAPPDATA", @"C:\Users\u\AppData\Local")),
            @"C:\Program Files\TgwsOne\", @"C:\Users\u");

        Assert.Equal(@"C:\Program Files\TgwsOne", p.AppDir);
        Assert.Equal(@"C:\Program Files\TgwsOne\tgws-engine.exe", p.Engine);
        Assert.Equal(@"C:\Users\u\AppData\Local\TgwsOne", p.DataDir);
        Assert.Equal(@"C:\Users\u\AppData\Local\TgwsOne\TgwsOne.ini", p.Ini);
        Assert.Equal(@"C:\Users\u\AppData\Local\TgwsOne\lists", p.ListsDir);
        Assert.Equal(@"C:\Users\u\AppData\Local\TgwsOne\logs", p.LogsDir);
        Assert.Equal(@"C:\Users\u\AppData\Local\TgwsOne\run", p.RunDir);
        Assert.Equal(@"C:\Users\u\AppData\Local\TgwsOne\run\engine.pid", p.PidFile);
        Assert.Equal(@"C:\Users\u\AppData\Local\TgwsOne\run\start.lock", p.StartLockFile);
        Assert.Equal(@"C:\Users\u\AppData\Local\TgwsOne\logs\app.log", p.AppLogFile);
        Assert.Equal(@"C:\Users\u\AppData\Local\TgwsOne\logs\proxy.log", p.ProxyLogFile);
        Assert.Equal(@"C:\Users\u\AppData\Local\TgwsOne\logs\engine-console.log", p.EngineConsoleLog);
    }

    [Fact]
    public void Linux_DefaultsFromHome()
    {
        var p = AppPaths.For(OsKind.Linux, Env(), "/opt/tgwsone/", "/home/u");

        Assert.Equal("/opt/tgwsone", p.AppDir);
        Assert.Equal(Path.Join("/opt/tgwsone", "tgws-engine"), p.Engine);
        Assert.Equal(Path.Join("/home/u", ".config", "tgwsone"), p.DataDir);
        Assert.Equal(Path.Join("/home/u", ".config", "tgwsone", "tgwsone.ini"), p.Ini);
        Assert.Equal(Path.Join("/home/u", ".config", "tgwsone", "lists"), p.ListsDir);
        Assert.Equal(Path.Join("/home/u", ".local", "state", "tgwsone", "logs"), p.LogsDir);
        Assert.Equal(Path.Join("/home/u", ".local", "state", "tgwsone", "run"), p.RunDir);
        Assert.Equal(Path.Join("/home/u", ".local", "state", "tgwsone", "run", "engine.pid"), p.PidFile);
        Assert.Equal(Path.Join("/home/u", ".local", "state", "tgwsone", "logs", "app.log"), p.AppLogFile);
        Assert.Equal(Path.Join("/home/u", ".local", "state", "tgwsone", "logs", "proxy.log"), p.ProxyLogFile);
        Assert.Equal(Path.Join("/home/u", ".local", "state", "tgwsone", "logs", "engine-console.log"), p.EngineConsoleLog);
    }

    [Fact]
    public void Linux_XdgOverrides()
    {
        var p = AppPaths.For(OsKind.Linux,
            Env(("XDG_CONFIG_HOME", "/x/cfg"), ("XDG_STATE_HOME", "/x/state"), ("XDG_RUNTIME_DIR", "/x/run")),
            "/opt/tgwsone", "/home/u");

        Assert.Equal(Path.Join("/x/cfg", "tgwsone"), p.DataDir);
        Assert.Equal(Path.Join("/x/cfg", "tgwsone", "tgwsone.ini"), p.Ini);
        Assert.Equal(Path.Join("/x/state", "tgwsone", "logs"), p.LogsDir);
        Assert.Equal(Path.Join("/x/run", "tgwsone"), p.RunDir);
        Assert.Equal(Path.Join("/x/run", "tgwsone", "engine.pid"), p.PidFile);
        Assert.Equal(Path.Join("/x/run", "tgwsone", "start.lock"), p.StartLockFile);
    }

    [Fact]
    public void Linux_RelativeXdgValuesAreIgnored()
    {
        // The XDG spec says relative paths are invalid and must be ignored.
        var p = AppPaths.For(OsKind.Linux, Env(("XDG_CONFIG_HOME", "rel/cfg"), ("XDG_STATE_HOME", "")),
            "/opt/tgwsone", "/home/u");

        Assert.Equal(Path.Join("/home/u", ".config", "tgwsone"), p.DataDir);
        Assert.Equal(Path.Join("/home/u", ".local", "state", "tgwsone", "logs"), p.LogsDir);
    }

    [Theory]
    [InlineData(OsKind.Windows, @"D:\t\x", '\\', "TgwsOne.ini", "tgws-engine.exe")]
    [InlineData(OsKind.Linux, "/tmp/x", '/', "tgwsone.ini", "tgws-engine")]
    public void DataDirOverride_PutsEverythingUnderOneDir(OsKind os, string dir, char sep, string iniName, string engineName)
    {
        var p = AppPaths.For(os, Env(("TGWS_DATA_DIR", dir), ("XDG_RUNTIME_DIR", "/x/run"), ("LOCALAPPDATA", @"C:\L")),
            os == OsKind.Windows ? @"C:\app" : "/app", os == OsKind.Windows ? @"C:\Users\u" : "/home/u");

        Assert.Equal(dir, p.DataDir);
        Assert.Equal($"{dir}{sep}{iniName}", p.Ini);
        Assert.Equal($"{dir}{sep}lists", p.ListsDir);
        Assert.Equal($"{dir}{sep}logs", p.LogsDir);
        Assert.Equal($"{dir}{sep}run", p.RunDir);
        Assert.Equal($"{dir}{sep}run{sep}engine.pid", p.PidFile);
        Assert.Equal($"{dir}{sep}logs{sep}app.log", p.AppLogFile);
        // The engine stays next to the exe, unaffected by the data override.
        Assert.EndsWith($"{sep}{engineName}", p.Engine);
    }

    [Theory]
    [InlineData(OsKind.Windows)]
    [InlineData(OsKind.Linux)]
    public void EngineOverride_Wins(OsKind os)
    {
        var p = AppPaths.For(os, Env(("TGWS_ENGINE", "/custom/engine")), "/app", "/home/u");

        Assert.Equal("/custom/engine", p.Engine);
    }
}
