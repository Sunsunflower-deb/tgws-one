using System.Runtime.Versioning;
using System.Text;
using TgwsOne.Core;
using TgwsOne.Core.Zapret;
using TgwsOne.Services;

namespace TgwsOne.Tests;

/// <summary>
/// <c>--admin</c> is what runs with administrator rights, and what it writes is run as root by the service: so these tests
/// are about what it refuses as much as what it does.
/// </summary>
public class AdminCliTests
{
    private const UnixFileMode Mode644 = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead | UnixFileMode.OtherRead;
    private const UnixFileMode Mode755 = Mode644 | UnixFileMode.UserExecute | UnixFileMode.GroupExecute | UnixFileMode.OtherExecute;

    /// <summary>
    /// The machine as the helper sees it under pkexec: <c>PKEXEC_UID</c> says who asked, and the file handed over (by its path, and
    /// again by its open handle) is an ordinary file owned by <paramref name="owner"/> unless told otherwise.
    /// </summary>
    private static AdminHost Host(string? pkexecUid = "1000", string? sudoUid = null, uint owner = 1000, int type = UnixFileMeta.Regular, uint? openedOwner = null)
        => new(n => n switch { "PKEXEC_UID" => pkexecUid, "SUDO_UID" => sudoUid, _ => null },
               _ => new UnixFileMeta(owner, 0x1A4, type),
               _ => new UnixFileMeta(openedOwner ?? owner, 0x1A4));

    private sealed class Rig : IDisposable
    {
        private readonly TempDir _tmp = new();

        public ServicePaths Paths { get; }
        public string StrategiesDir => Paths.StrategiesDir;

        public Rig()
        {
            Paths = ServicePaths.For(OsKind.Linux, n => n == "TGWS_SERVICE_ROOT" ? _tmp.Combine("service") : null);
            if (OperatingSystem.IsLinux()) File.SetUnixFileMode(_tmp.Path, Mode755);   // whatever umask the tests run under: others must reach the files
        }

        public string Combine(params string[] parts) => _tmp.Combine(parts);

        /// <summary>A file with the given text (the strategy as the window hands it over).</summary>
        public string Input(string text, string name = "input.args")
        {
            string path = _tmp.Combine("in", name);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, text, new UTF8Encoding(false));
            return path;
        }

        /// <summary>A file a strategy can name; <paramref name="mode"/> is its permissions.</summary>
        [SupportedOSPlatform("linux")]
        public string BundleFile(string name, UnixFileMode mode = Mode644)
        {
            string path = _tmp.Combine("bundle", "files", name);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            foreach (string dir in new[] { _tmp.Combine("bundle"), _tmp.Combine("bundle", "files") }) File.SetUnixFileMode(dir, Mode755);
            File.WriteAllText(path, "x");
            File.SetUnixFileMode(path, mode);
            return path;
        }

        public (int Code, string Out, string Err) Run(params string[] args) => RunAs(Host(), args);

        public (int Code, string Out, string Err) RunAs(AdminHost host, params string[] args)
        {
            var o = new StringWriter();
            var e = new StringWriter();
            int code = AdminCli.Run(args, Paths, OsKind.Linux, o, e, host);
            return (code, o.ToString().TrimEnd(), e.ToString().TrimEnd());
        }

        public string Strategy(string name) => Path.Combine(StrategiesDir, name + ".args");

        public void Dispose() => _tmp.Dispose();
    }

    // ----- saving -----

    [Fact]
    public void Save_WritesTheStrategy_ReadableByTheService_AndSaysSo()
    {
        using var rig = new Rig();
        string input = rig.Input("# мой провайдер\n--lua-desync=multisplit:pos=1\n--payload=tls_client_hello\n");

        (int code, string stdout, string stderr) = rig.Run("save-strategy", "my-isp", input);

        Assert.Equal((0, "Стратегия «my-isp» сохранена.", ""), (code, stdout, stderr));
        Assert.Equal("# мой провайдер\n--lua-desync=multisplit:pos=1\n--payload=tls_client_hello\n", File.ReadAllText(rig.Strategy("my-isp")));
        if (OperatingSystem.IsLinux()) Assert.Equal(Mode644, File.GetUnixFileMode(rig.Strategy("my-isp")) & (UnixFileMode)0x1FF);
    }

    [Fact]
    public void Save_CreatesTheFolder_OpenForEveryoneToRead()
    {
        if (!OperatingSystem.IsLinux()) return;
        using var rig = new Rig();
        Assert.False(Directory.Exists(rig.StrategiesDir));

        rig.Run("save-strategy", "a", rig.Input("--lua-desync=multisplit:pos=1"));

        const UnixFileMode everyone = UnixFileMode.GroupRead | UnixFileMode.GroupExecute | UnixFileMode.OtherRead | UnixFileMode.OtherExecute;
        Assert.Equal(everyone, File.GetUnixFileMode(rig.StrategiesDir) & everyone);   // nfqws2 reaches it after dropping root
    }

    [Fact]
    public void Save_TheFileEndsWithOneNewline_WhateverTheInputDid()
    {
        using var rig = new Rig();

        rig.Run("save-strategy", "a", rig.Input("--lua-desync=multisplit:pos=1\n\n\n"));
        rig.Run("save-strategy", "b", rig.Input("--lua-desync=multisplit:pos=1"));

        Assert.Equal("--lua-desync=multisplit:pos=1\n", File.ReadAllText(rig.Strategy("a")));
        Assert.Equal("--lua-desync=multisplit:pos=1\n", File.ReadAllText(rig.Strategy("b")));
    }

    [Fact]
    public void Save_ReplacesAnExistingOne_AndLeavesNoTemporaryFileBehind()
    {
        using var rig = new Rig();
        rig.Run("save-strategy", "my-isp", rig.Input("--lua-desync=multisplit:pos=1"));

        (int code, _, _) = rig.Run("save-strategy", "my-isp", rig.Input("--lua-desync=multisplit:pos=2"));

        Assert.Equal(0, code);
        Assert.Equal("--lua-desync=multisplit:pos=2\n", File.ReadAllText(rig.Strategy("my-isp")));
        Assert.Equal([rig.Strategy("my-isp")], Directory.GetFiles(rig.StrategiesDir));
    }

    [Fact]
    public void ASavedStrategy_IsOneTheServiceAccepts()
    {
        // The proof that it is the same rule on both sides: the service's own loader reads back what was written.
        using var rig = new Rig();
        rig.Run("save-strategy", "my-isp", rig.Input("--lua-desync=multisplit:pos=1\n--payload=tls_client_hello\n"));

        (DesyncPart? part, string? error) = CustomStrategies.Load(rig.StrategiesDir, "my-isp", OsKind.Linux, _ => new UnixFileMeta(0, 0x1A4));

        Assert.Null(error);
        Assert.Equal(["--lua-desync=multisplit:pos=1", "--payload=tls_client_hello"], part!.Args);
    }

    // ----- what it refuses -----

    [Theory]
    [InlineData("")]
    [InlineData("MyIsp")]
    [InlineData("my isp")]
    [InlineData("../evil")]
    [InlineData("a/b")]
    [InlineData("имя")]
    [InlineData("a.b")]
    [InlineData("this-name-is-far-too-long-for-a-strategy")]
    public void Save_ABadName_IsRefused_AndNothingIsWritten(string name)
    {
        using var rig = new Rig();

        (int code, _, string stderr) = rig.Run("save-strategy", name, rig.Input("--lua-desync=multisplit:pos=1"));

        Assert.Equal(1, code);
        Assert.StartsWith("недопустимое имя", stderr);
        Assert.False(Directory.Exists(rig.StrategiesDir));
    }

    [Theory]
    [InlineData("--debug=@/tmp/leak\n--lua-desync=multisplit:pos=1")]
    [InlineData("--qnum=1\n--lua-desync=multisplit:pos=1")]
    [InlineData("--filter-tcp=80\n--lua-desync=multisplit:pos=1")]
    [InlineData("--ipset=/etc/shadow\n--lua-desync=multisplit:pos=1")]
    [InlineData("--daemon\n--lua-desync=multisplit:pos=1")]
    [InlineData("@/etc/some-config\n--lua-desync=multisplit:pos=1")]
    public void Save_AForbiddenOption_IsRefused_AndNothingIsWritten(string text)
    {
        using var rig = new Rig();

        (int code, _, string stderr) = rig.Run("save-strategy", "x", rig.Input(text));

        Assert.Equal(1, code);
        Assert.StartsWith("недопустимый параметр", stderr);
        Assert.False(Directory.Exists(rig.StrategiesDir));
    }

    [Fact]
    public void Save_WithoutAnyDesyncStep_IsRefused()
    {
        using var rig = new Rig();

        (int code, _, string stderr) = rig.Run("save-strategy", "x", rig.Input("# только комментарий\n--payload=tls_client_hello\n"));

        Assert.Equal(1, code);
        Assert.Contains("--lua-desync", stderr);
        Assert.False(Directory.Exists(rig.StrategiesDir));
    }

    [Fact]
    public void Save_ARefusedStrategy_DoesNotDisturbTheOneThatWasThere()
    {
        using var rig = new Rig();
        rig.Run("save-strategy", "x", rig.Input("--lua-desync=multisplit:pos=1"));

        (int code, _, _) = rig.Run("save-strategy", "x", rig.Input("--debug=@/tmp/leak"));

        Assert.Equal(1, code);
        Assert.Equal("--lua-desync=multisplit:pos=1\n", File.ReadAllText(rig.Strategy("x")));
    }

    [Fact]
    public void Save_ATooBigFile_IsRefused()
    {
        using var rig = new Rig();
        string input = rig.Input("--lua-desync=multisplit:pos=1\n" + new string('#', (int)AdminCli.MaxStrategyBytes));

        (int code, _, string stderr) = rig.Run("save-strategy", "x", input);

        Assert.Equal(1, code);
        Assert.Equal("файл слишком большой для стратегии", stderr);
    }

    [Fact]
    public void Save_AMissingFile_IsRefused()
    {
        using var rig = new Rig();

        (int code, _, string stderr) = rig.Run("save-strategy", "x", rig.Combine("in", "nope.args"));

        Assert.Equal(1, code);
        Assert.StartsWith("файл со стратегией не найден", stderr);
    }

    [Fact]
    public void Save_AFileThatIsNotText_IsRefused()
    {
        using var rig = new Rig();
        string path = rig.Combine("in", "bin.args");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, [(byte)'-', (byte)'-', 0xC3, 0x28, 0x80, 0xFF]);   // not UTF-8, and no byte-order mark to say it is something else

        (int code, _, string stderr) = rig.Run("save-strategy", "x", path);

        Assert.Equal(1, code);
        Assert.StartsWith("не удалось прочитать файл со стратегией", stderr);
    }

    // ----- whose file it is (Linux: the helper runs as root, so it reads only what the person who asked for it owns) -----

    private const string Strategy1 = "--lua-desync=multisplit:pos=1\n";

    [Fact]
    public void Save_AFileOfSomebodyElse_IsRefused_AndNothingIsWritten()
    {
        using var rig = new Rig();

        (int code, _, string stderr) = rig.RunAs(Host(pkexecUid: "1000", owner: 0), "save-strategy", "x", rig.Input(Strategy1));

        Assert.Equal(1, code);
        Assert.Contains("принадлежит другому пользователю", stderr);
        Assert.False(Directory.Exists(rig.StrategiesDir));
    }

    [Fact]
    public void Save_AFileSwappedForSomebodyElsesAfterTheCheck_IsRefused_ByItsOpenHandle()
    {
        // The path looked fine when it was checked; by the time it was opened it led to another file.
        using var rig = new Rig();

        (int code, _, string stderr) = rig.RunAs(Host(owner: 1000, openedOwner: 0), "save-strategy", "x", rig.Input(Strategy1));

        Assert.Equal(1, code);
        Assert.Contains("принадлежит другому пользователю", stderr);
        Assert.False(Directory.Exists(rig.StrategiesDir));
    }

    [Fact]
    public void Save_ASymlink_IsRefused()
    {
        using var rig = new Rig();

        (int code, _, string stderr) = rig.RunAs(Host(type: UnixFileMeta.Symlink), "save-strategy", "x", rig.Input(Strategy1));

        Assert.Equal(1, code);
        Assert.Contains("символическая ссылка", stderr);
        Assert.False(Directory.Exists(rig.StrategiesDir));
    }

    [Theory]
    [InlineData(0x1000)]   // a pipe
    [InlineData(0x4000)]   // a directory
    [InlineData(0x6000)]   // a block device
    public void Save_SomethingThatIsNotAnOrdinaryFile_IsRefused(int type)
    {
        using var rig = new Rig();

        (int code, _, string stderr) = rig.RunAs(Host(type: type), "save-strategy", "x", rig.Input(Strategy1));

        Assert.Equal(1, code);
        Assert.Contains("не обычный файл", stderr);
    }

    [Fact]
    public void Save_WithoutKnowingWhoAsked_IsRefused()
    {
        // Neither pkexec nor sudo started it: nobody to compare the file's owner with.
        using var rig = new Rig();

        (int code, _, string stderr) = rig.RunAs(Host(pkexecUid: null, sudoUid: null), "save-strategy", "x", rig.Input(Strategy1));

        Assert.Equal(1, code);
        Assert.Contains("pkexec или sudo", stderr);
        Assert.False(Directory.Exists(rig.StrategiesDir));
    }

    [Fact]
    public void Save_UnderSudo_UsesSudoUid()
    {
        using var rig = new Rig();

        (int code, _, _) = rig.RunAs(Host(pkexecUid: null, sudoUid: "1000", owner: 1000), "save-strategy", "x", rig.Input(Strategy1));
        (int other, _, _) = rig.RunAs(Host(pkexecUid: null, sudoUid: "1001", owner: 1000), "save-strategy", "y", rig.Input(Strategy1));

        Assert.Equal((0, 1), (code, other));
    }

    [Fact]
    public void Save_PkexecUid_WinsOverSudoUid()
    {
        using var rig = new Rig();

        (int code, _, _) = rig.RunAs(Host(pkexecUid: "1000", sudoUid: "1001", owner: 1000), "save-strategy", "x", rig.Input(Strategy1));

        Assert.Equal(0, code);
    }

    [Theory]
    [InlineData("abc")]
    [InlineData("-1")]
    [InlineData("1000x")]
    [InlineData("99999999999")]
    public void Save_AnUnreadableUid_IsRefused(string uid)
    {
        using var rig = new Rig();

        (int code, _, string stderr) = rig.RunAs(Host(pkexecUid: uid, sudoUid: "1000"), "save-strategy", "x", rig.Input(Strategy1));

        Assert.Equal(1, code);
        Assert.Contains("pkexec или sudo", stderr);   // a bad PKEXEC_UID is not quietly replaced by SUDO_UID
    }

    [Fact]
    public void Save_RealFiles_OwnedByTheCaller_AreAccepted_ASymlinkToThemIsNot()
    {
        if (!OperatingSystem.IsLinux()) return;
        using var rig = new Rig();
        string input = rig.Input(Strategy1);
        uint me = UnixFiles.Lstat(input)!.OwnerUid;
        AdminHost real = AdminHost.Real with { GetEnv = n => n == "PKEXEC_UID" ? me.ToString() : null };
        string link = rig.Combine("in", "link.args");
        File.CreateSymbolicLink(link, input);

        (int direct, _, _) = rig.RunAs(real, "save-strategy", "x", input);
        (int viaLink, _, string linkErr) = rig.RunAs(real, "save-strategy", "y", link);

        Assert.Equal((0, 1), (direct, viaLink));
        Assert.Contains("символическая ссылка", linkErr);
        Assert.False(File.Exists(rig.Strategy("y")));
    }

    [Fact]
    public void Save_ARealFileOfRoot_IsRefused_WhenTheCallerIsNotRoot()
    {
        // /etc/passwd is everybody's to read, and still not a file to take a strategy from on somebody else's word.
        if (!OperatingSystem.IsLinux() || !File.Exists("/etc/passwd")) return;
        using var rig = new Rig();
        uint me = UnixFiles.Lstat(rig.Input(Strategy1))!.OwnerUid;
        if (me == 0) return;
        AdminHost real = AdminHost.Real with { GetEnv = n => n == "PKEXEC_UID" ? me.ToString() : null };

        (int code, _, string stderr) = rig.RunAs(real, "save-strategy", "x", "/etc/passwd");

        Assert.Equal(1, code);
        Assert.Contains("принадлежит другому пользователю", stderr);
    }

    // ----- the files a strategy names (Linux: nfqws2 drops root before it opens them) -----

    [Fact]
    public void Save_AFileOthersCanRead_IsAccepted()
    {
        if (!OperatingSystem.IsLinux()) return;
        using var rig = new Rig();
        string blob = rig.BundleFile("tls_mine.bin");

        (int code, _, _) = rig.Run("save-strategy", "x", rig.Input($"--blob=mine:@{blob}\n--lua-desync=fake:blob=mine:repeats=2"));

        Assert.Equal(0, code);
    }

    [Fact]
    public void Save_AFileOnlyRootCanRead_IsRefused_WithTheFix()
    {
        if (!OperatingSystem.IsLinux()) return;
        using var rig = new Rig();
        string blob = rig.BundleFile("secret.bin", UnixFileMode.UserRead | UnixFileMode.UserWrite);

        (int code, _, string stderr) = rig.Run("save-strategy", "x", rig.Input($"--blob=mine:@{blob}\n--lua-desync=fake:blob=mine"));

        Assert.Equal(1, code);
        Assert.Contains(blob, stderr);
        Assert.Contains("chmod o+r", stderr);
        Assert.False(Directory.Exists(rig.StrategiesDir));
    }

    [Fact]
    public void Save_AFileInAFolderOthersCannotEnter_IsRefused()
    {
        if (!OperatingSystem.IsLinux()) return;
        using var rig = new Rig();
        string blob = rig.BundleFile("tls.bin");
        File.SetUnixFileMode(rig.Combine("bundle", "files"), UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);   // 0700

        (int code, _, string stderr) = rig.Run("save-strategy", "x", rig.Input($"--blob=mine:@{blob}\n--lua-desync=fake:blob=mine"));

        Assert.Equal(1, code);
        Assert.Contains("chmod o+x", stderr);
    }

    [Fact]
    public void Save_ALuaFileIsChecked_AsWell()
    {
        if (!OperatingSystem.IsLinux()) return;
        using var rig = new Rig();
        string lua = rig.BundleFile("mine.lua", UnixFileMode.UserRead | UnixFileMode.UserWrite);

        (int code, _, string stderr) = rig.Run("save-strategy", "x", rig.Input($"--lua-init=@{lua}\n--lua-desync=mine"));

        Assert.Equal(1, code);
        Assert.Contains("chmod o+r", stderr);
    }

    [Fact]
    public void Save_AMissingFileOrARelativePath_IsRefused()
    {
        if (!OperatingSystem.IsLinux()) return;
        using var rig = new Rig();

        (int missing, _, string missingMsg) = rig.Run("save-strategy", "x", rig.Input("--blob=mine:@/no/such/file.bin\n--lua-desync=fake:blob=mine"));
        (int relative, _, string relativeMsg) = rig.Run("save-strategy", "x", rig.Input("--blob=mine:@files/tls.bin\n--lua-desync=fake:blob=mine"));

        Assert.Equal((1, 1), (missing, relative));
        Assert.Contains("не найден", missingMsg);
        Assert.Contains("полный путь", relativeMsg);
    }

    [Fact]
    public void Save_ALiteralBlob_NeedsNoFile()
    {
        if (!OperatingSystem.IsLinux()) return;
        using var rig = new Rig();

        (int code, _, _) = rig.Run("save-strategy", "x", rig.Input("--blob=zeros:0x00000000\n--lua-desync=fake:blob=zeros"));

        Assert.Equal(0, code);
    }

    // ----- deleting -----

    [Fact]
    public void Delete_RemovesTheStrategy()
    {
        using var rig = new Rig();
        rig.Run("save-strategy", "my-isp", rig.Input("--lua-desync=multisplit:pos=1"));

        (int code, string stdout, _) = rig.Run("delete-strategy", "my-isp");

        Assert.Equal((0, "Стратегия «my-isp» удалена."), (code, stdout));
        Assert.False(File.Exists(rig.Strategy("my-isp")));
    }

    [Fact]
    public void Delete_OfOneThatIsNotThere_IsAllRight()
    {
        using var rig = new Rig();

        (int code, string stdout, _) = rig.Run("delete-strategy", "nothing");

        Assert.Equal((0, "Стратегии «nothing» нет: удалять нечего."), (code, stdout));
    }

    [Theory]
    [InlineData("../service/state.ini")]
    [InlineData("A")]
    [InlineData("")]
    public void Delete_ABadName_IsRefused_AndCannotReachOtherFiles(string name)
    {
        using var rig = new Rig();
        File.WriteAllText(rig.Combine("keep.txt"), "x");

        (int code, _, _) = rig.Run("delete-strategy", name);

        Assert.Equal(1, code);
        Assert.True(File.Exists(rig.Combine("keep.txt")));
    }

    // ----- how it is called -----

    [Theory]
    [InlineData]
    [InlineData("frobnicate")]
    [InlineData("save-strategy")]
    [InlineData("save-strategy", "only-a-name")]
    [InlineData("delete-strategy")]
    [InlineData("delete-strategy", "a", "b")]
    public void ABadCall_IsUsage_Exit64(params string[] args)
    {
        using var rig = new Rig();

        (int code, string stdout, string stderr) = rig.Run(args);

        Assert.Equal(64, code);
        Assert.Equal("", stdout);
        Assert.Contains("--admin", stderr);
    }

    [Fact]
    public void TheMessage_AlsoGoesToTheOutFile_ForAProcessWithNoConsole()
    {
        using var rig = new Rig();
        string result = rig.Combine("out", "result.txt");
        Directory.CreateDirectory(Path.GetDirectoryName(result)!);

        rig.Run("save-strategy", "x", rig.Input("--lua-desync=multisplit:pos=1"), "--out", result);
        Assert.Equal("Стратегия «x» сохранена.\n", File.ReadAllText(result));

        (int code, _, _) = rig.Run("save-strategy", "Bad Name", rig.Input("--lua-desync=multisplit:pos=1"), "--out", result);
        Assert.Equal(1, code);
        Assert.StartsWith("недопустимое имя", File.ReadAllText(result));   // a refusal gets there too
    }

    [Fact]
    public void AnOutFileThatCannotBeWritten_DoesNotFailTheCommand()
    {
        using var rig = new Rig();

        (int code, string stdout, _) = rig.Run("save-strategy", "x", rig.Input("--lua-desync=multisplit:pos=1"), "--out", rig.Combine("no", "such", "dir", "r.txt"));

        Assert.Equal((0, "Стратегия «x» сохранена."), (code, stdout));
    }
}
