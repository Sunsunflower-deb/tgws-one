using System.Diagnostics;
using System.Text.RegularExpressions;
using TgwsOne.Core.Zapret;

namespace TgwsOne.Core.Tests.Zapret;

/// <summary>A fact that needs Linux and must not run as root: the host would then do real nft/sysctl work.</summary>
public sealed class NonRootLinuxFactAttribute : FactAttribute
{
    public NonRootLinuxFactAttribute()
    {
        if (!OperatingSystem.IsLinux()) Skip = "Linux only";
        else if (LibC.GetEffectiveUserId() == 0) Skip = "refuses to run as root: the service would touch the real nftables and sysctls";
    }
}

/// <summary>
/// The real <c>TgwsOne.dll --service</c> as a separate process: the same wiring systemd runs, with FakeZapret as the
/// executable and a fake <c>nft</c> first in PATH that records what it is asked to do (so nothing real is touched).
/// </summary>
[Collection(EnvCollection.Name)]
public class ServiceHostTests : IDisposable
{
    private readonly FakeZapretEnv _env = new();

    public void Dispose() => _env.Dispose();

    private static bool WaitFor(Func<bool> condition, int timeoutMs = 20000) => TestEnv.WaitFor(condition, timeoutMs);

    private (int Code, string Out, string Err) Cli(string root, params string[] args)
    {
        var psi = new ProcessStartInfo(Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") ?? "dotnet")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        psi.ArgumentList.Add(Path.Combine(AppContext.BaseDirectory, "TgwsOne.dll"));
        psi.ArgumentList.Add("--zapret");
        foreach (string a in args) psi.ArgumentList.Add(a);
        psi.Environment["TGWS_SERVICE_ROOT"] = root;
        using Process p = Process.Start(psi)!;
        Task<string> err = p.StandardError.ReadToEndAsync();
        string o = p.StandardOutput.ReadToEnd();
        Assert.True(p.WaitForExit(30_000));
        return (p.ExitCode, o, err.GetAwaiter().GetResult());
    }

    [NonRootLinuxFact]
    public async Task Service_RunsEndToEnd_AndCleansUpOnSigterm()
    {
        if (OperatingSystem.IsWindows()) return;   // the fact is skipped there; this tells the analyzer
        using var tmp = new TempDir();
        string root = tmp.Combine("root");
        string fakeBin = tmp.Combine("bin");
        string nftLog = tmp.Combine("nft.log");
        string debugLog = tmp.Combine("debug.log");
        Directory.CreateDirectory(fakeBin);
        File.WriteAllText(Path.Combine(fakeBin, "nft"), "#!/bin/sh\necho \"ARGS: $*\" >> \"$NFT_LOG\"\nif [ \"$1\" = \"-f\" ] && [ \"$2\" = \"-\" ]; then cat >> \"$NFT_LOG\"; fi\nexit 0\n");
        File.SetUnixFileMode(Path.Combine(fakeBin, "nft"), UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        ServicePaths paths = ServicePaths.For(OsKind.Linux, n => n == "TGWS_SERVICE_ROOT" ? root : null);
        // The first SP2 build left the logs folder 0700; the service has to open it for the app's Журналы page.
        Directory.CreateDirectory(paths.LogsDir);
        File.SetUnixFileMode(paths.LogsDir, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);

        var psi = new ProcessStartInfo(Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") ?? "dotnet")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        psi.ArgumentList.Add(Path.Combine(AppContext.BaseDirectory, "TgwsOne.dll"));
        psi.ArgumentList.Add("--service");
        psi.Environment["TGWS_SERVICE_ROOT"] = root;
        psi.Environment["TGWS_ZAPRET_EXE"] = FakeZapretEnv.ExePath;
        psi.Environment["TGWS_ZAPRET_DIR"] = tmp.Combine("bundle");
        psi.Environment["TGWS_ZAPRET_DEBUG"] = debugLog;
        psi.Environment["NFT_LOG"] = nftLog;
        psi.Environment["PATH"] = fakeBin + Path.PathSeparator + Environment.GetEnvironmentVariable("PATH");

        using Process service = Process.Start(psi)!;
        Task<string> serviceOut = service.StandardOutput.ReadToEndAsync();
        Task<string> serviceErr = service.StandardError.ReadToEndAsync();
        try
        {
            Assert.True(WaitFor(() => File.Exists(paths.Endpoint) || service.HasExited), "the control socket never appeared");
            if (service.HasExited) Assert.Fail($"service exited early ({service.ExitCode}): {await serviceOut} {await serviceErr}");

            // Started with the saved defaults: on, auto. nft was told first, then the child came up with the debug option.
            (int c1, string status, _) = Cli(root, "status");
            Assert.Equal(0, c1);
            Assert.Contains("обход DPI: работает", status);
            Assert.Contains("стратегия: auto", status);
            Assert.True(WaitFor(() => _env.Args.Length > 0));
            Assert.Equal("--debug=@" + debugLog, _env.Args[0]);
            string nft = File.ReadAllText(nftLog);
            Assert.StartsWith("ARGS: -f -\ntable inet tgwsone\ndelete table inet tgwsone\ntable inet tgwsone {", nft);
            Assert.Contains("queue num 3443 bypass", nft);

            (int c2, string use, _) = Cli(root, "use", "split");
            Assert.Equal(0, c2);
            Assert.Contains("стратегия: split", use);
            Assert.True(WaitFor(() => _env.Args.Any(a => a.Contains("seqovl=681", StringComparison.Ordinal))));

            (int c3, _, string badErr) = Cli(root, "use", "nonsense");
            Assert.Equal(1, c3);
            Assert.Contains("неизвестная стратегия", badErr);

            int pid = int.Parse(Regex.Match(Cli(root, "status").Out, @"процесс: (\d+)").Groups[1].Value);
            Assert.True(FakeZapretEnv.IsAlive(pid));

            (int c4, string off, _) = Cli(root, "off");
            Assert.Equal(0, c4);
            Assert.Contains("обход DPI: выключен", off);
            Assert.True(WaitFor(() => !FakeZapretEnv.IsAlive(pid)), "off must stop the child");
            Assert.Contains("ARGS: delete table inet tgwsone", File.ReadAllText(nftLog));

            (int c5, string on, _) = Cli(root, "on");
            Assert.Equal(0, c5);
            Assert.Contains("обход DPI: работает", on);
            int pid2 = int.Parse(Regex.Match(on, @"процесс: (\d+)").Groups[1].Value);
            Assert.True(FakeZapretEnv.IsAlive(pid2));

            // The state survives: a restart of the service picks up "split" and on, without being told again.
            Assert.Equal("split", ServiceState.Load(paths.StateIni).Strategy);

            // SIGTERM is how systemd stops it: the child goes, the nft table goes, the socket goes, exit code 0.
            Assert.True(LibC.Kill(service.Id, LibC.SigTerm));
            Assert.True(service.WaitForExit(20_000), "the service did not stop on SIGTERM");
            Assert.Equal(0, service.ExitCode);
            Assert.True(WaitFor(() => !FakeZapretEnv.IsAlive(pid2)), "the child outlived the service");
            Assert.False(File.Exists(paths.Endpoint));
            string[] nftCalls = File.ReadAllLines(nftLog).Where(l => l.StartsWith("ARGS:", StringComparison.Ordinal)).ToArray();
            Assert.Equal("ARGS: delete table inet tgwsone", nftCalls[^1]);
            Assert.True(File.Exists(Path.Combine(paths.LogsDir, "service.log")));
            Assert.True(File.Exists(Path.Combine(paths.LogsDir, "zapret.log")));

            // Any user can read both logs (the Журналы page): the folder is opened, the files are readable by group and others.
            const UnixFileMode everyone = UnixFileMode.GroupRead | UnixFileMode.GroupExecute | UnixFileMode.OtherRead | UnixFileMode.OtherExecute;
            Assert.Equal(everyone, File.GetUnixFileMode(paths.LogsDir) & everyone);
            foreach (string name in new[] { "service.log", "zapret.log" })
            {
                const UnixFileMode read = UnixFileMode.GroupRead | UnixFileMode.OtherRead;
                Assert.Equal(read, File.GetUnixFileMode(Path.Combine(paths.LogsDir, name)) & read);
            }
        }
        finally
        {
            if (!service.HasExited) service.Kill(entireProcessTree: true);
        }
    }

    [NonRootLinuxFact]
    public void Service_ReplacesAStaleSocket_AndReportsAMissingBundleAsAnErrorState()
    {
        // A socket left by a crashed run must not keep the service from starting, and a missing bundle is a state to show.
        using var tmp = new TempDir();
        string root = tmp.Combine("root");
        Directory.CreateDirectory(root);
        ServicePaths paths = ServicePaths.For(OsKind.Linux, n => n == "TGWS_SERVICE_ROOT" ? root : null);
        File.WriteAllText(paths.Endpoint, "left behind");

        var psi = new ProcessStartInfo(Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") ?? "dotnet")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        psi.ArgumentList.Add(Path.Combine(AppContext.BaseDirectory, "TgwsOne.dll"));
        psi.ArgumentList.Add("--service");
        psi.Environment["TGWS_SERVICE_ROOT"] = root;
        psi.Environment["TGWS_ZAPRET_EXE"] = tmp.Combine("no-such-nfqws2");   // the bundle is missing: an error state, not a crash
        psi.Environment["TGWS_ZAPRET_DIR"] = tmp.Combine("bundle");
        using Process service = Process.Start(psi)!;
        _ = service.StandardOutput.ReadToEndAsync();
        _ = service.StandardError.ReadToEndAsync();
        try
        {
            Assert.True(WaitFor(() =>
            {
                if (service.HasExited) return true;
                try { return Cli(root, "status").Code == 0; } catch { return false; }
            }), "no answer from the service");
            Assert.False(service.HasExited);

            (int code, string status, _) = Cli(root, "status");
            Assert.Equal(0, code);
            Assert.Contains("обход DPI: ошибка", status);
            Assert.Contains("пакет zapret2 не найден", status);
        }
        finally
        {
            if (!service.HasExited) service.Kill(entireProcessTree: true);
        }
    }
}
