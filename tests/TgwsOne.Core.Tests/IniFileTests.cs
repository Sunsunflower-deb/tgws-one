namespace TgwsOne.Core.Tests;

public class IniFileTests
{
    private static string Fixture => Path.Combine(AppContext.BaseDirectory, "Fixtures", "tgwsone-1.0.6.ini");

    [Fact]
    public void Load_106Fixture_ReadsAllKeys()
    {
        var ini = IniFile.Load(Fixture);

        Assert.Equal("127.0.0.1", ini.Get("proxy", "host"));
        Assert.Equal("1443", ini.Get("proxy", "port"));
        Assert.Equal("2:149.154.167.220,4:149.154.167.220", ini.Get("proxy", "dcmap"));
        Assert.Equal("256", ini.Get("proxy", "bufkb"));
        Assert.Equal("fake-tls", ini.Get("zapret", "strategy"));
        Assert.Equal("443", ini.Get("zapret", "tcpports"));
        Assert.Equal("0123456789abcdef0123456789abcdef", ini.Get("app", "secret"));
        Assert.Equal("1", ini.Get("app", "traytip"));
        Assert.Null(ini.Get("app", "nope"));
        Assert.Null(ini.Get("nosection", "port"));
    }

    [Fact]
    public void RoundTrip_106Fixture_PreservesKeysOrderAndSections()
    {
        using var tmp = new TempDir();
        string copy = tmp.Combine("copy.ini");
        File.Copy(Fixture, copy);
        var original = IniFile.Load(Fixture);

        IniFile.Load(copy).Save();
        var reloaded = IniFile.Load(copy);

        // Same (section, key, value) sequence: order and the [zapret] section both survive.
        Assert.Equal(original.Entries().ToList(), reloaded.Entries().ToList());
        Assert.Contains(reloaded.Entries(), e => e is ("zapret", "strategy", "fake-tls"));
    }

    [Fact]
    public void EmptyValue_IsKeptAsEmpty()
    {
        using var tmp = new TempDir();
        string copy = tmp.Combine("copy.ini");
        File.Copy(Fixture, copy);

        var ini = IniFile.Load(copy);
        Assert.Equal("", ini.Get("proxy", "cfdomains"));

        ini.Set("proxy", "dcmap", "");
        ini.Save();
        var reloaded = IniFile.Load(copy);

        Assert.Equal("", reloaded.Get("proxy", "dcmap"));
        Assert.Equal("", reloaded.Get("proxy", "cfdomains"));
    }

    [Fact]
    public void Keys_AreCaseInsensitive()
    {
        var ini = IniFile.Load(Fixture);

        Assert.Equal("1443", ini.Get("PROXY", "Port"));

        ini.Set("Proxy", "PORT", "2000");
        Assert.Equal("2000", ini.Get("proxy", "port"));
        Assert.Single(ini.Entries(), e => e.Section == "proxy" && e.Key == "port");
    }

    [Fact]
    public void CommentsAndJunkLines_AreSkipped()
    {
        using var tmp = new TempDir();
        string path = tmp.Combine("a.ini");
        File.WriteAllText(path, string.Join("\n",
            "; comment", "# another", "", "orphan=first", "[Proxy]", "  Port = 99  ", "no equals here", "=novalue",
            "[broken", "host=h"));

        var ini = IniFile.Load(path);

        Assert.Equal("first", ini.Get("app", "orphan"));        // keys before any section go to [app]
        Assert.Equal("99", ini.Get("proxy", "port"));           // trimmed, section lower-cased
        Assert.Equal("h", ini.Get("proxy", "host"));            // "[broken" is no header and has no "=": skipped
        Assert.Equal(3, ini.Entries().Count());                 // orphan, port, host; the junk lines left nothing
    }

    [Fact]
    public void Set_NewKeyAndSection_Appended()
    {
        var ini = IniFile.Load(Fixture);

        ini.Set("proxy", "extra", "x");      // new key lands in its own section, not at the end of the file
        ini.Set("newsec", "k", "v");         // new section goes last

        var entries = ini.Entries().ToList();
        int extra = entries.FindIndex(e => e.Key == "extra");
        int logmax = entries.FindIndex(e => e.Key == "logmaxmb");
        Assert.Equal(logmax + 1, extra);
        Assert.Equal(("newsec", "k", "v"), entries[^1]);
    }

    [Fact]
    public void Set_MultilineValue_Throws()
    {
        var ini = IniFile.Load(Fixture);

        Assert.Throws<ArgumentException>(() => ini.Set("app", "secret", "a\n[proxy]\nport=1"));
    }

    [Fact]
    public void Load_MissingFile_IsEmpty()
    {
        using var tmp = new TempDir();

        var ini = IniFile.Load(tmp.Combine("nope.ini"));

        Assert.Empty(ini.Entries());
        Assert.Null(ini.Get("proxy", "port"));
    }

    [Fact]
    public void Save_CreatesDirectory()
    {
        using var tmp = new TempDir();
        string path = tmp.Combine("a", "b", "c.ini");
        var ini = IniFile.Load(path);
        ini.Set("app", "secret", "abc");

        ini.Save();

        Assert.True(File.Exists(path));
        Assert.Equal("abc", IniFile.Load(path).Get("app", "secret"));
        Assert.False(File.Exists(path + ".tmp"));
        Assert.False(File.ReadAllBytes(path).AsSpan().StartsWith(new byte[] { 0xEF, 0xBB, 0xBF }));
    }
}
