using TgwsOne.Core.Zapret;

namespace TgwsOne.Core.Tests.Zapret;

public class CustomStrategiesTests
{
    private static UnixFileMeta Root(int mode = 0x1A4 /* 0644 */) => new(0, mode);

    [Fact]
    public void Parse_ValidFile()
    {
        const string text = """
            # a strategy written by an admin
            --lua-desync=multisplit:pos=2

            --payload=tls_client_hello
            --out-range=-d10
              --in-range=x
            --blob=mine:@/etc/tgwsone/strategies/mine.bin
            --lua-init=@/etc/tgwsone/strategies/mine.lua
            --lua-desync=fake:blob=mine:repeats=3
            """;
        (DesyncPart? part, string? error) = CustomStrategies.Parse(text.Replace("\n", "\r\n"));

        Assert.Null(error);
        Assert.NotNull(part);
        Assert.Empty(part.Blobs);
        Assert.Equal(
        [
            "--lua-desync=multisplit:pos=2",
            "--payload=tls_client_hello",
            "--out-range=-d10",
            "--in-range=x",
            "--blob=mine:@/etc/tgwsone/strategies/mine.bin",
            "--lua-init=@/etc/tgwsone/strategies/mine.lua",
            "--lua-desync=fake:blob=mine:repeats=3",
        ], part.Args);
    }

    [Theory]
    [InlineData("--filter-tcp=443", "--filter-tcp")]
    [InlineData("--filter-l7=tls", "--filter-l7")]
    [InlineData("--ipset=x", "--ipset")]
    [InlineData("--hostlist=x", "--hostlist")]
    [InlineData("--hostlist-auto=x", "--hostlist-auto")]
    [InlineData("--new", "--new")]
    [InlineData("--wf-tcp-out=443", "--wf-tcp-out")]
    [InlineData("--qnum=1", "--qnum")]
    [InlineData("--debug=1", "--debug")]
    [InlineData("@cfg", "@cfg")]
    [InlineData("--LUA-DESYNC=fake", "--LUA-DESYNC")]
    public void Parse_ForbiddenOption_Rejected(string line, string option)
    {
        (DesyncPart? part, string? error) = CustomStrategies.Parse("--lua-desync=multisplit:pos=1\n" + line + "\n");

        Assert.Null(part);
        Assert.Equal($"недопустимый параметр в стратегии: {option}", error);
    }

    [Theory]
    [InlineData("--lua-desync")]
    [InlineData("--lua-desync=")]
    [InlineData("--payload=")]
    public void Parse_OptionWithoutValue_Rejected(string line)
    {
        (DesyncPart? part, string? error) = CustomStrategies.Parse("--lua-desync=multisplit\n" + line + "\n");

        Assert.Null(part);
        Assert.Contains("нет значения", error);
        Assert.Contains(line.TrimEnd('='), error);
    }

    [Theory]
    [InlineData("")]
    [InlineData("# only a comment\n\n")]
    [InlineData("--payload=tls_client_hello\n--out-range=-d10\n")]
    public void Parse_WithoutAnyStep_Rejected(string text)
    {
        (DesyncPart? part, string? error) = CustomStrategies.Parse(text);

        Assert.Null(part);
        Assert.Contains("--lua-desync", error);
    }

    [Theory]
    [InlineData("a", true)]
    [InlineData("my-strategy-2", true)]
    [InlineData("0", true)]
    [InlineData("-x-", true)]
    [InlineData("abcdefghijklmnopqrstuvwxyz0123456", false)]   // 33
    [InlineData("abcdefghijklmnopqrstuvwxyz012345", true)]     // 32
    [InlineData("", false)]
    [InlineData("Mine", false)]
    [InlineData("a_b", false)]
    [InlineData("a.b", false)]
    [InlineData("../x", false)]
    [InlineData("a b", false)]
    [InlineData("a\n", false)]
    [InlineData("стратегия", false)]
    public void Name_Validation(string name, bool valid) => Assert.Equal(valid, CustomStrategies.IsValidName(name));

    [Fact]
    public void List_Linux_SkipsNonRootOrWritable()
    {
        using var tmp = new TempDir();
        string[] files = ["good.args", "other-owner.args", "group-writable.args", "world-writable.args", "private.args", "Bad_Name.args", "notes.txt", "a.b.args"];
        foreach (string f in files) File.WriteAllText(tmp.Combine(f), "--lua-desync=multisplit\n");
        Directory.CreateDirectory(tmp.Combine("dir.args"));

        var metas = new Dictionary<string, UnixFileMeta>
        {
            ["good.args"] = Root(0x1A4),                 // 0644
            ["other-owner.args"] = new(1000, 0x1A4),
            ["group-writable.args"] = Root(0x1B4),       // 0664
            ["world-writable.args"] = Root(0x1B6),       // 0666
            ["private.args"] = Root(0x180),              // 0600
            ["Bad_Name.args"] = Root(),
            ["a.b.args"] = Root(),
        };
        var seen = new List<string>();
        UnixFileMeta? Stat(string path)
        {
            seen.Add(Path.GetFileName(path));
            return metas.GetValueOrDefault(Path.GetFileName(path));
        }

        Assert.Equal(["good", "private"], CustomStrategies.List(tmp.Path, OsKind.Linux, Stat));
        Assert.DoesNotContain("notes.txt", seen);   // not even looked at
    }

    [Fact]
    public void List_Linux_UnreadableMetadata_IsSkipped()
    {
        using var tmp = new TempDir();
        File.WriteAllText(tmp.Combine("x.args"), "--lua-desync=multisplit\n");

        Assert.Empty(CustomStrategies.List(tmp.Path, OsKind.Linux, _ => null));
    }

    [Fact]
    public void List_Windows_DoesNoOwnershipCheck()
    {
        using var tmp = new TempDir();
        File.WriteAllText(tmp.Combine("b.args"), "--lua-desync=multisplit\n");
        File.WriteAllText(tmp.Combine("a.args"), "--lua-desync=multisplit\n");

        Assert.Equal(["a", "b"], CustomStrategies.List(tmp.Path, OsKind.Windows, _ => throw new InvalidOperationException("must not stat")));
    }

    [Fact]
    public void List_MissingDirectory_IsEmpty()
        => Assert.Empty(CustomStrategies.List("/no/such/dir-" + Guid.NewGuid().ToString("N"), OsKind.Linux, _ => Root()));

    [Fact]
    public void Load_Valid_Linux()
    {
        using var tmp = new TempDir();
        File.WriteAllText(tmp.Combine("mine.args"), "# mine\n--lua-desync=multisplit:pos=3\n");

        (DesyncPart? part, string? error) = CustomStrategies.Load(tmp.Path, "mine", OsKind.Linux, _ => Root());

        Assert.Null(error);
        Assert.Equal(["--lua-desync=multisplit:pos=3"], part!.Args);
    }

    [Fact]
    public void Load_Missing_Error()
    {
        using var tmp = new TempDir();

        (DesyncPart? part, string? error) = CustomStrategies.Load(tmp.Path, "nope", OsKind.Linux, _ => Root());

        Assert.Null(part);
        Assert.Equal("стратегия nope не найдена", error);
    }

    [Theory]
    [InlineData("../etc/passwd")]
    [InlineData("A")]
    [InlineData("")]
    public void Load_BadName_Error(string name)
    {
        using var tmp = new TempDir();

        (DesyncPart? part, string? error) = CustomStrategies.Load(tmp.Path, name, OsKind.Linux, _ => Root());

        Assert.Null(part);
        Assert.StartsWith("недопустимое имя стратегии", error);
    }

    [Fact]
    public void Load_Linux_NotRootOwned_Rejected()
    {
        using var tmp = new TempDir();
        File.WriteAllText(tmp.Combine("mine.args"), "--lua-desync=multisplit\n");

        (DesyncPart? part, string? error) = CustomStrategies.Load(tmp.Path, "mine", OsKind.Linux, _ => new UnixFileMeta(1000, 0x1A4));

        Assert.Null(part);
        Assert.Equal("стратегия mine отклонена: файл должен принадлежать root и не быть доступен на запись другим", error);
    }

    [Fact]
    public void Load_Linux_WorldWritable_Rejected()
    {
        using var tmp = new TempDir();
        File.WriteAllText(tmp.Combine("mine.args"), "--lua-desync=multisplit\n");

        (DesyncPart? part, string? error) = CustomStrategies.Load(tmp.Path, "mine", OsKind.Linux, _ => Root(0x1B6));

        Assert.Null(part);
        Assert.Contains("отклонена", error);
    }

    [Fact]
    public void Load_Windows_SkipsOwnershipCheck()
    {
        using var tmp = new TempDir();
        File.WriteAllText(tmp.Combine("mine.args"), "--lua-desync=multisplit\n");

        (DesyncPart? part, string? error) = CustomStrategies.Load(tmp.Path, "mine", OsKind.Windows, _ => throw new InvalidOperationException("must not stat"));

        Assert.Null(error);
        Assert.NotNull(part);
    }

    [Fact]
    public void Load_ForbiddenOption_ReturnsTheParseError()
    {
        using var tmp = new TempDir();
        File.WriteAllText(tmp.Combine("mine.args"), "--lua-desync=multisplit\n--hostlist=/etc/shadow\n");

        (DesyncPart? part, string? error) = CustomStrategies.Load(tmp.Path, "mine", OsKind.Linux, _ => Root());

        Assert.Null(part);
        Assert.Equal("недопустимый параметр в стратегии: --hostlist", error);
    }

    [Fact]
    public void RealStat_ReportsOwnerAndMode()
    {
        if (!OperatingSystem.IsLinux()) return;

        UnixFileMeta? passwd = LibC.Stat("/etc/passwd");
        Assert.NotNull(passwd);
        Assert.Equal(0u, passwd.OwnerUid);
        Assert.Equal(0, passwd.Mode & 0x12);   // root's passwd file is not group/other writable

        using var tmp = new TempDir();
        string file = tmp.Combine("f");
        File.WriteAllText(file, "x");
        File.SetUnixFileMode(file, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.OtherWrite);
        UnixFileMeta? mine = LibC.Stat(file);
        Assert.NotNull(mine);
        Assert.Equal(0x182, mine.Mode);   // 0602
        Assert.Equal(LibC.GetEffectiveUserId(), mine.OwnerUid);

        Assert.Null(LibC.Stat(tmp.Combine("missing")));
    }
}
