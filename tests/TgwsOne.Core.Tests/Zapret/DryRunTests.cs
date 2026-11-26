using System.Diagnostics;
using TgwsOne.Core.Zapret;

namespace TgwsOne.Core.Tests.Zapret;

/// <summary>A theory that runs only where the Linux zapret2 bundle has been fetched (<c>make zapret2</c>).</summary>
public sealed class Nfqws2TheoryAttribute : TheoryAttribute
{
    public Nfqws2TheoryAttribute()
    {
        if (!OperatingSystem.IsLinux() || TestRepo.Zapret2Linux is null)
            Skip = "needs out/zapret2/linux-x64/nfqws2 (make zapret2) on Linux";
    }
}

/// <summary>A fact that runs only where the Linux zapret2 bundle has been fetched (<c>make zapret2</c>).</summary>
public sealed class Nfqws2FactAttribute : FactAttribute
{
    public Nfqws2FactAttribute()
    {
        if (!OperatingSystem.IsLinux() || TestRepo.Zapret2Linux is null)
            Skip = "needs out/zapret2/linux-x64/nfqws2 (make zapret2) on Linux";
    }
}

/// <summary>
/// nfqws2 --dry-run needs no root: it parses every option and checks that the files exist.
/// It does not load Lua, so the Lua semantics are checked by the root namespace script.
/// </summary>
public class DryRunTests
{
    public static IEnumerable<object[]> Strategies => BuiltInStrategies.Ids.Select(id => new object[] { id });

    [Nfqws2Theory, MemberData(nameof(Strategies))]
    public async Task EveryBuiltIn_PassesNfqws2DryRun(string id)
    {
        using var tmp = new TempDir();
        (int code, string output) = await DryRunAsync(tmp, id, custom: null);
        Assert.True(code == 0, $"nfqws2 --dry-run rejected '{id}':\n{output}");
        Assert.Contains("command line parameters verified", output);
    }

    [Nfqws2Fact]
    public async Task CustomStrategy_WithItsOwnBlob_PassesNfqws2DryRun()
    {
        // The body is repeated in both profiles; the blob must still be declared once, or nfqws2 refuses the name.
        using var tmp = new TempDir();
        string blobFile = tmp.Combine("mine.bin");
        File.WriteAllBytes(blobFile, [1, 2, 3]);
        var custom = new DesyncPart([], [$"--blob=mine:@{blobFile}", "--lua-desync=multisplit:pos=2:seqovl=3:seqovl_pattern=mine"]);

        (int code, string output) = await DryRunAsync(tmp, "custom:mine", custom);
        Assert.True(code == 0, $"nfqws2 --dry-run rejected the custom strategy:\n{output}");
        Assert.Contains("command line parameters verified", output);
    }

    private static async Task<(int Code, string Output)> DryRunAsync(TempDir tmp, string id, DesyncPart? custom)
    {
        string bundleDir = tmp.Combine("zapret2");
        CopyDirectory(TestRepo.Zapret2Linux!, bundleDir);

        string lists = tmp.Combine("lists");
        Directory.CreateDirectory(lists);
        string[] files = ["cloudflare-ips.txt", "telegram.txt", "exclude-hosts.txt", "exclude-ips.txt", "cfproxy-hosts.txt"];
        foreach (string f in files) File.WriteAllText(Path.Combine(lists, f), "# test\n");
        // The narrow scope is the default of Build: it adds a profile for the proxy's own Cloudflare hosts, and nfqws2
        // has to accept a hostlist it can match.
        File.WriteAllText(Path.Combine(lists, files[4]), "# test\nkws2.pclead.co.uk\n");
        var zl = new ZapretLists(Path.Combine(lists, files[0]), Path.Combine(lists, files[1]), Path.Combine(lists, files[2]),
                                 Path.Combine(lists, files[3]), Path.Combine(lists, files[4]));

        // nfqws2 creates the --writable directory itself, but not its parent: the service makes the state dir.
        string stateDir = tmp.Combine("state");
        Directory.CreateDirectory(stateDir);

        var bundle = ZapretBundle.Locate(OsKind.Linux, tmp.Path, n => n == "TGWS_ZAPRET_DIR" ? bundleDir : null);
        IReadOnlyList<string> argv = ZapretArgs.Build(OsKind.Linux, bundle, zl, id, stateDir, custom);

        var psi = new ProcessStartInfo(bundle.Exe)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        psi.ArgumentList.Add("--dry-run");
        foreach (string a in argv) psi.ArgumentList.Add(a);

        using Process p = Process.Start(psi)!;
        Task<string> stdout = p.StandardOutput.ReadToEndAsync();
        Task<string> stderr = p.StandardError.ReadToEndAsync();
        await p.WaitForExitAsync();
        return (p.ExitCode, await stdout + await stderr);
    }

    private static void CopyDirectory(string from, string to)
    {
        Directory.CreateDirectory(to);
        foreach (string file in Directory.GetFiles(from))
            File.Copy(file, Path.Combine(to, Path.GetFileName(file)));
        foreach (string dir in Directory.GetDirectories(from))
            CopyDirectory(dir, Path.Combine(to, Path.GetFileName(dir)));
    }
}
