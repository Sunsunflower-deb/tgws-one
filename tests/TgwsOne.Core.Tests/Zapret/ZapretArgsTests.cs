using TgwsOne.Core.Zapret;

namespace TgwsOne.Core.Tests.Zapret;

public class ZapretArgsTests
{
    private static readonly ZapretLists LinuxLists = new(
        "/etc/tgwsone/lists/cloudflare-ips.txt", "/etc/tgwsone/lists/telegram.txt",
        "/var/lib/tgwsone/exclude-hosts.txt", "/var/lib/tgwsone/exclude-ips.txt",
        "/etc/tgwsone/lists/cfproxy-hosts.txt");

    private static readonly ZapretLists WindowsLists = new(
        @"C:\ProgramData\TgwsOne\lists\cloudflare-ips.txt", @"C:\ProgramData\TgwsOne\lists\telegram.txt",
        @"C:\ProgramData\TgwsOne\lists\exclude-hosts.txt", @"C:\ProgramData\TgwsOne\lists\exclude-ips.txt",
        @"C:\ProgramData\TgwsOne\lists\cfproxy-hosts.txt");

    private static ZapretBundle LinuxBundle() => ZapretBundle.Locate(OsKind.Linux, "/opt/tgwsone", _ => null);
    private static ZapretBundle WindowsBundle() => ZapretBundle.Locate(OsKind.Windows, @"C:\Program Files\TgwsOne", _ => null);

    private static string[] Linux(string id, DesyncPart? custom = null, string? debug = null, ZapretScope scope = ZapretScope.All)
        => [.. ZapretArgs.Build(OsKind.Linux, LinuxBundle(), LinuxLists, id, "/var/lib/tgwsone", custom, debug, scope)];

    [Fact]
    public void Build_Linux_FakeTls_ExactArgv()
    {
        Assert.Equal(
        [
            "--lua-init=@/opt/tgwsone/zapret2/lua/zapret-lib.lua",
            "--lua-init=@/opt/tgwsone/zapret2/lua/zapret-antidpi.lua",
            "--qnum=3443",
            "--filter-tcp=443",
            "--filter-l7=tls",
            "--ipset=/etc/tgwsone/lists/cloudflare-ips.txt",
            "--ipset-exclude=/var/lib/tgwsone/exclude-ips.txt",
            "--hostlist-exclude=/var/lib/tgwsone/exclude-hosts.txt",
            "--out-range=-d10",
            "--payload=tls_client_hello",
            "--lua-desync=fake:blob=0x00000000:tcp_seq=-10000:repeats=11",
            "--lua-desync=fake:blob=fake_default_tls:tcp_seq=-10000:repeats=11:tls_mod=rnd,dupsid,sni=www.google.com",
            "--lua-desync=multidisorder:pos=1,midsld",
            "--new",
            "--filter-tcp=443",
            "--filter-l7=tls",
            "--hostlist=/etc/tgwsone/lists/telegram.txt",
            "--hostlist-exclude=/var/lib/tgwsone/exclude-hosts.txt",
            "--out-range=-d10",
            "--payload=tls_client_hello",
            "--lua-desync=fake:blob=0x00000000:tcp_seq=-10000:repeats=11",
            "--lua-desync=fake:blob=fake_default_tls:tcp_seq=-10000:repeats=11:tls_mod=rnd,dupsid,sni=www.google.com",
            "--lua-desync=multidisorder:pos=1,midsld",
        ], Linux("fake-tls"));
    }

    [Fact]
    public void Build_Windows_FakeTls_ExactArgv()
    {
        string[] argv = [.. ZapretArgs.Build(OsKind.Windows, WindowsBundle(), WindowsLists, "fake-tls", @"C:\ProgramData\TgwsOne\state", scope: ZapretScope.All)];
        Assert.Equal(
        [
            @"--lua-init=@C:\Program Files\TgwsOne\zapret2\lua\zapret-lib.lua",
            @"--lua-init=@C:\Program Files\TgwsOne\zapret2\lua\zapret-antidpi.lua",
            "--wf-tcp-out=443",
            "--filter-tcp=443",
            "--filter-l7=tls",
            @"--ipset=C:\ProgramData\TgwsOne\lists\cloudflare-ips.txt",
            @"--ipset-exclude=C:\ProgramData\TgwsOne\lists\exclude-ips.txt",
            @"--hostlist-exclude=C:\ProgramData\TgwsOne\lists\exclude-hosts.txt",
            "--out-range=-d10",
            "--payload=tls_client_hello",
            "--lua-desync=fake:blob=0x00000000:tcp_seq=-10000:repeats=11",
            "--lua-desync=fake:blob=fake_default_tls:tcp_seq=-10000:repeats=11:tls_mod=rnd,dupsid,sni=www.google.com",
            "--lua-desync=multidisorder:pos=1,midsld",
            "--new",
            "--filter-tcp=443",
            "--filter-l7=tls",
            @"--hostlist=C:\ProgramData\TgwsOne\lists\telegram.txt",
            @"--hostlist-exclude=C:\ProgramData\TgwsOne\lists\exclude-hosts.txt",
            "--out-range=-d10",
            "--payload=tls_client_hello",
            "--lua-desync=fake:blob=0x00000000:tcp_seq=-10000:repeats=11",
            "--lua-desync=fake:blob=fake_default_tls:tcp_seq=-10000:repeats=11:tls_mod=rnd,dupsid,sni=www.google.com",
            "--lua-desync=multidisorder:pos=1,midsld",
        ], argv);
    }

    [Fact]
    public void Build_Linux_Auto_ExactArgv()
    {
        string[] profileBody =
        [
            "--in-range=-s5556",
            "--out-range=-d10",
            "--payload=all",
            "--lua-desync=circular:fails=3:time=60:key=tgws",
            "--in-range=x",
            "--payload=tls_client_hello",
            "--lua-desync=fake:blob=0x00000000:tcp_seq=-10000:repeats=11:strategy=1",
            "--lua-desync=fake:blob=fake_default_tls:tcp_seq=-10000:repeats=11:tls_mod=rnd,dupsid,sni=www.google.com:strategy=1",
            "--lua-desync=multidisorder:pos=1,midsld:strategy=1",
            "--lua-desync=multisplit:pos=1:seqovl=681:seqovl_pattern=tls_google:strategy=2",
            "--lua-desync=multisplit:pos=1:seqovl=568:seqovl_pattern=tls_vk:strategy=3",
            "--lua-desync=fake:blob=tls_google:tcp_md5:repeats=11:strategy=4",
            "--lua-desync=multisplit:pos=1:strategy=4",
        ];
        string[] expected =
        [
            "--lua-init=@/opt/tgwsone/zapret2/lua/zapret-lib.lua",
            "--lua-init=@/opt/tgwsone/zapret2/lua/zapret-antidpi.lua",
            "--lua-init=@/opt/tgwsone/zapret2/lua/zapret-auto.lua",
            "--writable=/var/lib/tgwsone/zapret-state",
            "--lua-init=@/opt/tgwsone/zapret2/lua/tgws-state.lua",
            "--blob=tls_google:@/opt/tgwsone/zapret2/files/fake/tls_clienthello_www_google_com.bin",
            "--blob=tls_vk:@/opt/tgwsone/zapret2/files/fake/tls_clienthello_vk_com.bin",
            "--qnum=3443",
            "--filter-tcp=443",
            "--filter-l7=tls",
            "--ipset=/etc/tgwsone/lists/cloudflare-ips.txt",
            "--ipset-exclude=/var/lib/tgwsone/exclude-ips.txt",
            "--hostlist-exclude=/var/lib/tgwsone/exclude-hosts.txt",
            .. profileBody,
            "--new",
            "--filter-tcp=443",
            "--filter-l7=tls",
            "--hostlist=/etc/tgwsone/lists/telegram.txt",
            "--hostlist-exclude=/var/lib/tgwsone/exclude-hosts.txt",
            .. profileBody,
        ];
        Assert.Equal(expected, Linux("auto"));
    }

    [Fact]
    public void Build_Windows_Auto_StateDirUsesBackslashes()
    {
        string[] argv = [.. ZapretArgs.Build(OsKind.Windows, WindowsBundle(), WindowsLists, "auto", @"C:\ProgramData\TgwsOne\state", scope: ZapretScope.All)];
        Assert.Contains(@"--writable=C:\ProgramData\TgwsOne\state\zapret-state", argv);
        Assert.Contains(@"--lua-init=@C:\Program Files\TgwsOne\zapret2\lua\tgws-state.lua", argv);
        Assert.Contains("--wf-tcp-out=443", argv);
    }

    [Fact]
    public void Build_NarrowScope_ProtectsTheProxyCloudflareHosts_AndOnlyThem()
    {
        // The narrow scope leaves other applications alone: no --ipset over all Cloudflare ranges. It must however
        // cover the proxy's own fallback (kws<dc>.<cf domain>), which is not a Telegram host name at all.
        string[] argv = Linux("split", scope: ZapretScope.Telegram);

        Assert.Contains("--hostlist=/etc/tgwsone/lists/cfproxy-hosts.txt", argv);
        Assert.Contains("--hostlist=/etc/tgwsone/lists/telegram.txt", argv);
        Assert.DoesNotContain(argv, a => a.StartsWith("--ipset=", StringComparison.Ordinal));
        Assert.DoesNotContain(argv, a => a.StartsWith("--ipset-exclude=", StringComparison.Ordinal));
        Assert.Single(argv, "--new");
    }

    [Fact]
    public void Build_NarrowScope_WithoutTheHostlist_SkipsThatProfile()
    {
        // Nothing generated yet (no list path): the run must still be a valid single-profile command line.
        var lists = new ZapretLists("/l/cloudflare-ips.txt", "/l/telegram.txt", "/l/exclude-hosts.txt", "/l/exclude-ips.txt");
        string[] argv = [.. ZapretArgs.Build(OsKind.Linux, LinuxBundle(), lists, "split", "/var/lib/tgwsone",
            scope: ZapretScope.Telegram)];

        Assert.DoesNotContain("--new", argv);
        Assert.Contains("--hostlist=/l/telegram.txt", argv);
    }

    [Fact]
    public void Build_WideScope_AddsTheCloudflareProfile()
    {
        string[] argv = Linux("split", scope: ZapretScope.All);

        Assert.Contains("--ipset=/etc/tgwsone/lists/cloudflare-ips.txt", argv);
        Assert.Contains("--new", argv);
    }

    [Fact]
    public void Build_PlainStrategy_HasNoStateOrAutoScripts()
    {
        string[] argv = Linux("split");
        Assert.DoesNotContain(argv, a => a.StartsWith("--writable", StringComparison.Ordinal));
        Assert.DoesNotContain(argv, a => a.Contains("zapret-auto.lua", StringComparison.Ordinal));
        Assert.DoesNotContain(argv, a => a.Contains("tgws-state.lua", StringComparison.Ordinal));
    }

    [Fact]
    public void Build_BlobsDeduplicated()
    {
        // auto uses tls_google twice (split, aggressive) and tls_vk once: each is declared once, in first-use order.
        string[] blobs = [.. Linux("auto").Where(a => a.StartsWith("--blob=", StringComparison.Ordinal))];
        Assert.Equal(
        [
            "--blob=tls_google:@/opt/tgwsone/zapret2/files/fake/tls_clienthello_www_google_com.bin",
            "--blob=tls_vk:@/opt/tgwsone/zapret2/files/fake/tls_clienthello_vk_com.bin",
        ], blobs);
    }

    [Fact]
    public void Build_Custom_ReplacesBody()
    {
        var custom = new DesyncPart([], ["--lua-desync=multisplit:pos=2", "--lua-desync=fake:blob=0x00:repeats=2"]);
        string[] argv = Linux("custom:mine", custom);

        string[] body =
        [
            "--out-range=-d10",
            "--payload=tls_client_hello",
            "--lua-desync=multisplit:pos=2",
            "--lua-desync=fake:blob=0x00:repeats=2",
        ];
        int firstNew = Array.IndexOf(argv, "--new");
        Assert.Equal(body, argv[(firstNew - body.Length)..firstNew]);
        Assert.Equal(body, argv[^body.Length..]);
        Assert.DoesNotContain(argv, a => a.Contains("multidisorder", StringComparison.Ordinal));
    }

    [Fact]
    public void Build_Custom_BlobAndLuaInitLines_GoToTheHeadOnce()
    {
        // Both profiles repeat the body, but nfqws2 rejects a blob name defined twice: global options are hoisted.
        var custom = new DesyncPart([], ["--blob=mine:@/etc/tgwsone/strategies/mine.bin", "--lua-init=@/etc/tgwsone/strategies/mine.lua", "--lua-desync=multisplit:pos=2"]);
        string[] argv = Linux("custom:mine", custom);

        Assert.Single(argv, "--blob=mine:@/etc/tgwsone/strategies/mine.bin");
        Assert.Single(argv, "--lua-init=@/etc/tgwsone/strategies/mine.lua");
        int qnum = Array.IndexOf(argv, "--qnum=3443");
        Assert.True(Array.IndexOf(argv, "--blob=mine:@/etc/tgwsone/strategies/mine.bin") < qnum);
        Assert.True(Array.IndexOf(argv, "--lua-init=@/etc/tgwsone/strategies/mine.lua") < qnum);
        Assert.Equal(2, argv.Count(a => a == "--lua-desync=multisplit:pos=2"));
    }

    [Fact]
    public void Build_Custom_WithoutPart_Throws()
        => Assert.Throws<ArgumentException>(() => Linux("custom:mine"));

    [Fact]
    public void Build_UnknownStrategy_Throws()
        => Assert.Throws<ArgumentException>(() => Linux("nonsense"));

    [Fact]
    public void Build_DebugFile_First()
    {
        string[] argv = Linux("split", debug: "/tmp/debug.log");
        Assert.Equal("--debug=@/tmp/debug.log", argv[0]);
        Assert.Equal("--lua-init=@/opt/tgwsone/zapret2/lua/zapret-lib.lua", argv[1]);
    }

    [Fact]
    public void Bundle_DefaultLayout_Linux()
    {
        ZapretBundle b = LinuxBundle();
        Assert.Equal("/opt/tgwsone/zapret2", b.Dir);
        Assert.Equal("/opt/tgwsone/zapret2/nfqws2", b.Exe);
        Assert.Equal("/opt/tgwsone/zapret2/lua", b.LuaDir);
        Assert.Equal("/opt/tgwsone/zapret2/files/fake", b.FakeDir);
    }

    [Fact]
    public void Bundle_DefaultLayout_Windows()
    {
        ZapretBundle b = WindowsBundle();
        Assert.Equal(@"C:\Program Files\TgwsOne\zapret2", b.Dir);
        Assert.Equal(@"C:\Program Files\TgwsOne\zapret2\winws2.exe", b.Exe);
        Assert.Equal(@"C:\Program Files\TgwsOne\zapret2\lua", b.LuaDir);
        Assert.Equal(@"C:\Program Files\TgwsOne\zapret2\files\fake", b.FakeDir);
    }

    [Fact]
    public void Bundle_EnvOverrides()
    {
        var both = ZapretBundle.Locate(OsKind.Linux, "/opt/tgwsone",
            n => n switch { "TGWS_ZAPRET_DIR" => "/x/z", "TGWS_ZAPRET_EXE" => "/x/fake", _ => null });
        Assert.Equal("/x/z", both.Dir);
        Assert.Equal("/x/fake", both.Exe);
        Assert.Equal("/x/z/lua", both.LuaDir);
        Assert.Equal("/x/z/files/fake", both.FakeDir);

        var dirOnly = ZapretBundle.Locate(OsKind.Linux, "/opt/tgwsone", n => n == "TGWS_ZAPRET_DIR" ? "/x/z" : null);
        Assert.Equal("/x/z/nfqws2", dirOnly.Exe);

        var exeOnly = ZapretBundle.Locate(OsKind.Linux, "/opt/tgwsone", n => n == "TGWS_ZAPRET_EXE" ? "/x/fake" : null);
        Assert.Equal("/opt/tgwsone/zapret2", exeOnly.Dir);
        Assert.Equal("/x/fake", exeOnly.Exe);

        var empty = ZapretBundle.Locate(OsKind.Linux, "/opt/tgwsone", _ => "");
        Assert.Equal("/opt/tgwsone/zapret2/nfqws2", empty.Exe);
    }

    [Fact]
    public void Bundle_Exists_FollowsTheExecutable()
    {
        using var tmp = new TempDir();
        string exe = tmp.Combine("nfqws2");
        var b = ZapretBundle.Locate(OsKind.Linux, "/opt/tgwsone", n => n == "TGWS_ZAPRET_EXE" ? exe : null);
        Assert.False(b.Exists);
        File.WriteAllText(exe, "");
        Assert.True(b.Exists);
    }

    [Fact]
    public void Bundle_Version_MatchesTheLock()
        => Assert.Equal(TestRepo.LockValue("Z2_VERSION"), LinuxBundle().Version);
}
