namespace TgwsOne.Core.Zapret;

/// <summary>Where the pinned zapret2 release lives: the executable, the Lua scripts and the fake ClientHello files.</summary>
public sealed class ZapretBundle
{
    // Keep in step with Z2_VERSION in zapret2/zapret2.lock (a test compares them).
    private const string ReleaseVersion = "v1.0.7";

    private ZapretBundle(string dir, string exe, string luaDir, string fakeDir)
    {
        Dir = dir;
        Exe = exe;
        LuaDir = luaDir;
        FakeDir = fakeDir;
    }

    /// <summary>
    /// <c>&lt;appDir&gt;/zapret2</c> unless <c>TGWS_ZAPRET_DIR</c> says otherwise; <c>TGWS_ZAPRET_EXE</c> replaces
    /// just the executable (the tests substitute a fake one).
    /// </summary>
    public static ZapretBundle Locate(OsKind os, string appDir, Func<string, string?> env)
    {
        string dir = env("TGWS_ZAPRET_DIR") is { Length: > 0 } d ? d : OsPath.Join(os, appDir, "zapret2");
        string exe = env("TGWS_ZAPRET_EXE") is { Length: > 0 } e
            ? e
            : OsPath.Join(os, dir, os == OsKind.Windows ? "winws2.exe" : "nfqws2");
        return new ZapretBundle(dir, exe, OsPath.Join(os, dir, "lua"), OsPath.Join(os, dir, "files", "fake"));
    }

    public string Dir { get; }
    public string Exe { get; }
    public string LuaDir { get; }
    public string FakeDir { get; }

    public bool Exists => File.Exists(Exe);

    /// <summary>The bundled zapret2 release, for the status report. No process is run to find it out.</summary>
    public string Version => ReleaseVersion;
}
