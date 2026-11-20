using System.Net;

namespace TgwsOne.Core.Tests;

public class ProxySettingsTests
{
    private static string Fixture => Path.Combine(AppContext.BaseDirectory, "Fixtures", "tgwsone-1.0.6.ini");
    private const string Secret = "0123456789abcdef0123456789abcdef";

    private static ProxyForm DefaultForm() => new(
        Host: "127.0.0.1", Port: "1443", DcMap: "2:149.154.167.220\n4:149.154.167.220",
        CfProxy: true, H2: true, NoSecure: false, Verbose: true,
        CfDomains: "", WorkerDomains: "", BufKb: "256", PoolSize: "4", LogMaxMb: "5");

    private static void AssertSame(ProxySettings expected, ProxySettings actual)
    {
        Assert.Equal(expected.Host, actual.Host);
        Assert.Equal(expected.Port, actual.Port);
        Assert.Equal(expected.DcMap, actual.DcMap);                 // DcEntry is a record: element-wise equality
        Assert.Equal(expected.CfProxy, actual.CfProxy);
        Assert.Equal(expected.H2, actual.H2);
        Assert.Equal(expected.NoSecure, actual.NoSecure);
        Assert.Equal(expected.Verbose, actual.Verbose);
        Assert.Equal(expected.CfDomains, actual.CfDomains);
        Assert.Equal(expected.WorkerDomains, actual.WorkerDomains);
        Assert.Equal(expected.BufKb, actual.BufKb);
        Assert.Equal(expected.PoolSize, actual.PoolSize);
        Assert.Equal(expected.LogMaxMb, actual.LogMaxMb);
        Assert.Equal(expected.Secret, actual.Secret);
    }

    private static ProxySettings ParseOk(ProxyForm form)
    {
        var (settings, errors) = ProxySettingsStore.Parse(form, Secret);
        Assert.Empty(errors);
        return settings!;
    }

    // ---------- load ----------

    [Fact]
    public void Load_EmptyIni_GivesDefaults()
    {
        using var tmp = new TempDir();
        var ini = IniFile.Load(tmp.Combine("none.ini"));

        var s = ProxySettingsStore.Load(ini);

        Assert.Equal(IPAddress.Loopback, s.Host);
        Assert.Equal(1443, s.Port);
        Assert.Equal([new DcEntry(2, IPAddress.Parse("149.154.167.220")), new DcEntry(4, IPAddress.Parse("149.154.167.220"))], s.DcMap);
        Assert.True(s.CfProxy);
        Assert.True(s.H2);
        Assert.False(s.NoSecure);
        Assert.True(s.Verbose);
        Assert.Empty(s.CfDomains);
        Assert.Empty(s.WorkerDomains);
        Assert.Equal(256, s.BufKb);
        Assert.Equal(4, s.PoolSize);
        Assert.Equal(5, s.LogMaxMb);
        Assert.Equal("", s.Secret);   // Load never invents a secret; EnsureSecret does
    }

    [Fact]
    public void Load_106Fixture()
    {
        var s = ProxySettingsStore.Load(IniFile.Load(Fixture));

        Assert.Equal(IPAddress.Loopback, s.Host);
        Assert.Equal(1443, s.Port);
        Assert.Equal(2, s.DcMap.Count);
        Assert.Equal(Secret, s.Secret);
        Assert.True(s.Verbose);
        Assert.Empty(s.CfDomains);
    }

    [Fact]
    public void Load_GarbagePort_FallsBackTo1443()
    {
        using var tmp = new TempDir();
        var ini = IniFile.Load(tmp.Combine("a.ini"));
        ini.Set("proxy", "port", "abc");
        ini.Set("proxy", "bufkb", "1");        // below the minimum: also falls back
        ini.Set("proxy", "host", "localhost");  // not an IP: also falls back

        var s = ProxySettingsStore.Load(ini);

        Assert.Equal(1443, s.Port);
        Assert.Equal(256, s.BufKb);
        Assert.Equal(IPAddress.Loopback, s.Host);
    }

    [Fact]
    public void Load_EmptyDcMap_StaysEmpty()
    {
        using var tmp = new TempDir();
        var ini = IniFile.Load(tmp.Combine("a.ini"));
        ini.Set("proxy", "dcmap", "");

        Assert.Empty(ProxySettingsStore.Load(ini).DcMap);
    }

    [Fact]
    public void Load_BoolSpellings()
    {
        using var tmp = new TempDir();
        var ini = IniFile.Load(tmp.Combine("a.ini"));
        ini.Set("proxy", "cfproxy", "YES");
        ini.Set("proxy", "h2", "off");       // anything but 1/true/yes is false
        ini.Set("proxy", "nosecure", "True");
        ini.Set("proxy", "verbose", "");      // empty → default (true)

        var s = ProxySettingsStore.Load(ini);

        Assert.True(s.CfProxy);
        Assert.False(s.H2);
        Assert.True(s.NoSecure);
        Assert.True(s.Verbose);
    }

    [Fact]
    public void ReadForm_EmptyIni_GivesDefaultsWithDcMapOnePerLine()
    {
        using var tmp = new TempDir();

        var form = ProxySettingsStore.ReadForm(IniFile.Load(tmp.Combine("none.ini")));

        Assert.Equal(DefaultForm(), form);
    }

    // ---------- parse + save ----------

    [Fact]
    public void Parse_Valid_RoundTripsThroughSave()
    {
        var form = DefaultForm() with
        {
            Host = "0.0.0.0", Port = "2080", DcMap = "1:1.2.3.4, 5:5.6.7.8\r\n", CfProxy = false, H2 = false, NoSecure = true,
            Verbose = false, CfDomains = "a.com, b.com;a.com", WorkerDomains = "w.dev", BufKb = "64", PoolSize = "0", LogMaxMb = "9",
        };
        var parsed = ParseOk(form);
        using var tmp = new TempDir();
        string path = tmp.Combine("a.ini");

        ProxySettingsStore.Save(IniFile.Load(path), parsed);
        var loaded = ProxySettingsStore.Load(IniFile.Load(path));

        AssertSame(parsed, loaded);
        Assert.Equal("1:1.2.3.4,5:5.6.7.8", IniFile.Load(path).Get("proxy", "dcmap"));
        Assert.Equal("a.com,b.com", IniFile.Load(path).Get("proxy", "cfdomains"));
        Assert.Equal(Secret, IniFile.Load(path).Get("app", "secret"));
    }

    [Theory]
    [InlineData("0")]
    [InlineData("65536")]
    [InlineData("abc")]
    [InlineData("")]
    public void Parse_Port_Invalid(string port)
    {
        var (s, errors) = ProxySettingsStore.Parse(DefaultForm() with { Port = port }, Secret);

        Assert.Null(s);
        Assert.Equal([new FieldError("Port", "Порт должен быть числом от 1 до 65535.")], errors);
    }

    [Fact]
    public void Parse_Host_NotIp()
    {
        var (s, errors) = ProxySettingsStore.Parse(DefaultForm() with { Host = "localhost" }, Secret);

        Assert.Null(s);
        Assert.Equal([new FieldError("Host", "Хост должен быть IP-адресом, например 127.0.0.1.")], errors);
    }

    [Theory]
    [InlineData("149.154.167.220")]
    [InlineData(":149.154.167.220")]
    public void Parse_Dc_NoColon(string line)
    {
        var (_, errors) = ProxySettingsStore.Parse(DefaultForm() with { DcMap = "2:1.1.1.1\n" + line }, Secret);

        Assert.Equal([new FieldError("DcMap",
            "Строка DC → IP должна быть в формате номер:IP, например 4:149.154.167.220. Проблемная строка: " + line)], errors);
    }

    [Fact]
    public void Parse_Dc_BadNumber()
    {
        var (_, errors) = ProxySettingsStore.Parse(DefaultForm() with { DcMap = "x:1.2.3.4" }, Secret);

        Assert.Equal([new FieldError("DcMap", "Не удалось разобрать строку DC → IP: x:1.2.3.4")], errors);
    }

    [Fact]
    public void Parse_Dc_BadIp()
    {
        var (_, errors) = ProxySettingsStore.Parse(DefaultForm() with { DcMap = "2:999.1.1.1" }, Secret);

        Assert.Equal([new FieldError("DcMap", "Не удалось разобрать строку DC → IP: 2:999.1.1.1")], errors);
    }

    [Fact]
    public void Parse_Dc_EmptyIsValid_SavedAsEmpty()
    {
        var parsed = ParseOk(DefaultForm() with { DcMap = "  \r\n " });
        using var tmp = new TempDir();
        string path = tmp.Combine("a.ini");

        ProxySettingsStore.Save(IniFile.Load(path), parsed);

        Assert.Empty(parsed.DcMap);
        Assert.Equal("", IniFile.Load(path).Get("proxy", "dcmap"));   // present and empty, not absent
        Assert.Empty(ProxySettingsStore.Load(IniFile.Load(path)).DcMap);
    }

    [Fact]
    public void Parse_BufKb_Below4()
    {
        var (_, errors) = ProxySettingsStore.Parse(DefaultForm() with { BufKb = "3" }, Secret);

        Assert.Equal([new FieldError("BufKb", "Буфер — не меньше 4 КБ.")], errors);
    }

    [Fact]
    public void Parse_Pool_Negative()
    {
        var (_, errors) = ProxySettingsStore.Parse(DefaultForm() with { PoolSize = "-1" }, Secret);

        Assert.Equal([new FieldError("PoolSize", "Пул WS — не меньше 0.")], errors);
    }

    [Fact]
    public void Parse_LogMb_Zero()
    {
        var (_, errors) = ProxySettingsStore.Parse(DefaultForm() with { LogMaxMb = "0" }, Secret);

        Assert.Equal([new FieldError("LogMaxMb", "Размер лога — не меньше 1 МБ.")], errors);
    }

    [Fact]
    public void Parse_MultipleErrors_AllReported()
    {
        var (s, errors) = ProxySettingsStore.Parse(
            DefaultForm() with { Port = "0", Host = "nope", BufKb = "1", PoolSize = "-5", LogMaxMb = "0" }, Secret);

        Assert.Null(s);
        Assert.Equal(["Port", "Host", "BufKb", "PoolSize", "LogMaxMb"], errors.Select(e => e.Field));
    }

    [Fact]
    public void SplitList_SeparatorsAndDedup()
    {
        Assert.Equal(["a.com", "b.com", "c.com"], ProxySettingsStore.SplitList("a.com, b.com;a.com\tc.com"));
        Assert.Equal(["x"], ProxySettingsStore.SplitList("\r\nx\n x "));
        Assert.Empty(ProxySettingsStore.SplitList(null));
        Assert.Empty(ProxySettingsStore.SplitList(" ,; "));
    }

    // ---------- secret ----------

    [Fact]
    public void EnsureSecret_Missing_GeneratesAndSaves()
    {
        using var tmp = new TempDir();
        string path = tmp.Combine("a.ini");
        var log = new AppLog(tmp.Combine("app.log"));
        var ini = IniFile.Load(path);

        string secret = ProxySettingsStore.EnsureSecret(ini, log);

        Assert.True(ProxySettingsStore.IsValidSecret(secret));
        Assert.Equal(secret, IniFile.Load(path).Get("app", "secret"));   // saved to disk
        Assert.Contains("создан новый secret (сохранён в настройках)", File.ReadAllText(tmp.Combine("app.log")));
    }

    [Fact]
    public void EnsureSecret_Short_Regenerates()
    {
        using var tmp = new TempDir();
        var ini = IniFile.Load(tmp.Combine("a.ini"));
        ini.Set("app", "secret", "abc123");

        string secret = ProxySettingsStore.EnsureSecret(ini);

        Assert.NotEqual("abc123", secret);
        Assert.True(ProxySettingsStore.IsValidSecret(secret));
    }

    [Fact]
    public void EnsureSecret_32NonHex_Regenerates()
    {
        using var tmp = new TempDir();
        var ini = IniFile.Load(tmp.Combine("a.ini"));
        string bad = "zz" + new string('0', 30);
        ini.Set("app", "secret", bad);

        string secret = ProxySettingsStore.EnsureSecret(ini);

        Assert.NotEqual(bad, secret);
        Assert.True(ProxySettingsStore.IsValidSecret(secret));
    }

    [Fact]
    public void EnsureSecret_Valid_Kept()
    {
        using var tmp = new TempDir();
        string path = tmp.Combine("a.ini");
        File.Copy(Fixture, path);
        string before = File.ReadAllText(path);

        string secret = ProxySettingsStore.EnsureSecret(IniFile.Load(path));

        Assert.Equal(Secret, secret);
        Assert.Equal(before, File.ReadAllText(path));   // nothing rewritten
    }

    [Fact]
    public void NewSecret_Is32LowerHex()
    {
        string a = ProxySettingsStore.NewSecret();
        string b = ProxySettingsStore.NewSecret();

        Assert.Matches("^[0-9a-f]{32}$", a);
        Assert.NotEqual(a, b);
    }

    [Theory]
    [InlineData("0123456789abcdef0123456789abcdef", true)]
    [InlineData("0123456789ABCDEF0123456789ABCDEF", true)]
    [InlineData("0123456789abcdef0123456789abcde", false)]
    [InlineData("0123456789abcdef0123456789abcdef0", false)]
    [InlineData("0123456789abcdef0123456789abcdeg", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void IsValidSecret(string? s, bool expected) => Assert.Equal(expected, ProxySettingsStore.IsValidSecret(s));
}
