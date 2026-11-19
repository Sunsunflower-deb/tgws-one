using System.Diagnostics;
using TgwsOne.Core.Zapret;

namespace TgwsOne.Core.Tests.Zapret;

/// <summary>
/// <c>tgwsone --zapret …</c> against a <see cref="ZapretService"/> and a <see cref="ControlServer"/> running in this
/// process on a temp root. Output and exit codes are checked in-process; a few runs go through the real executable to
/// cover <c>Program</c>'s dispatch too.
/// </summary>
[Collection(EnvCollection.Name)]
public class CliZapretTests : IDisposable
{
    private readonly FakeZapretEnv _env = new();

    public void Dispose() => _env.Dispose();

    private sealed class Served : IAsyncDisposable
    {
        private readonly CancellationTokenSource _cts = new();
        private readonly Task _run;

        public Served(ServiceRig rig)
        {
            Rig = rig;
            var server = new ControlServer(rig.Paths, rig.Service, rig.Log);
            _run = server.RunAsync(_cts.Token);
            Listening = server.WhenListening;
        }

        public ServiceRig Rig { get; }
        public Task Listening { get; }
        public ServicePaths Paths => Rig.Paths;
        public string Root => Path.GetDirectoryName(Rig.Paths.StateIni)!;

        public async ValueTask DisposeAsync()
        {
            await _cts.CancelAsync();
            await _run;
            _cts.Dispose();
            await Rig.DisposeAsync();
        }
    }

    private async Task<Served> ServeAsync(Action<ServicePaths>? beforeStart = null, string[]? rootOwned = null, bool bundleMissing = false)
    {
        var served = new Served(await ServiceRig.StartAsync(_env, beforeStart, rootOwned, bundleMissing));
        await served.Listening;
        return served;
    }

    private static async Task<(int Code, string Out, string Err)> RunInProcess(ServicePaths paths, params string[] args)
    {
        var stdout = new StringWriter();
        var stderr = new StringWriter();
        int code = await ZapretCli.RunAsync(args, paths, stdout, stderr, TimeSpan.FromSeconds(5));
        return (code, stdout.ToString().Replace("\r\n", "\n"), stderr.ToString().Replace("\r\n", "\n"));
    }

    private static (int Code, string Out, string Err) RunProcess(string serviceRoot, params string[] args)
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
        psi.Environment["TGWS_SERVICE_ROOT"] = serviceRoot;

        using Process p = Process.Start(psi)!;
        Task<string> err = p.StandardError.ReadToEndAsync();
        string o = p.StandardOutput.ReadToEnd();
        Assert.True(p.WaitForExit(30_000), "CLI did not exit");
        return (p.ExitCode, o.Replace("\r\n", "\n"), err.GetAwaiter().GetResult().Replace("\r\n", "\n"));
    }

    [LinuxFact]
    public async Task Status_PrintsASummary_ExitZero()
    {
        await using Served s = await ServeAsync();

        (int code, string stdout, string stderr) = await RunInProcess(s.Paths, "status");

        Assert.Equal(0, code);
        Assert.Equal("", stderr);
        Assert.Contains("обход DPI: работает\n", stdout);
        Assert.Contains("стратегия: auto\n", stdout);
        Assert.Matches(@"процесс: \d+, с \d\d:\d\d:\d\d\n", stdout);
        Assert.Contains("пакет zapret2: v1.0.7\n", stdout);
        Assert.DoesNotContain("перезапусков", stdout);
        Assert.DoesNotContain("последняя ошибка", stdout);
    }

    [LinuxFact]
    public async Task Status_ShowsAutosChoicesPerHost()
    {
        await using Served s = await ServeAsync();
        Directory.CreateDirectory(Path.GetDirectoryName(s.Paths.AutoStateFile)!);
        File.WriteAllText(s.Paths.AutoStateFile, "b.example.com\t2\t1\na.example.com\t4\t0\n");

        (_, string stdout, _) = await RunInProcess(s.Paths, "status");

        Assert.Contains("авто, по хостам:\n  a.example.com  aggressive  (неудач: 0)\n  b.example.com  split  (неудач: 1)\n", stdout);
    }

    [LinuxFact]
    public async Task Status_ShowsTheLastError()
    {
        await using Served s = await ServeAsync(bundleMissing: true);

        (int code, string stdout, _) = await RunInProcess(s.Paths, "status");

        Assert.Equal(0, code);   // the question was answered; the answer happens to be bad news
        Assert.Contains("обход DPI: ошибка\n", stdout);
        Assert.Contains("последняя ошибка:\n  пакет zapret2 не найден: ", stdout);
    }

    [LinuxFact]
    public async Task UseSplit_ExitZero_ShowsTheNewStrategy()
    {
        await using Served s = await ServeAsync();

        (int code, string stdout, string stderr) = await RunInProcess(s.Paths, "use", "split");

        Assert.Equal(0, code);
        Assert.Equal("", stderr);
        Assert.Contains("стратегия: split\n", stdout);
        Assert.Equal("split", ServiceState.Load(s.Paths.StateIni).Strategy);
    }

    [LinuxFact]
    public async Task UseNonsense_ExitOne_MessageOnStderr()
    {
        await using Served s = await ServeAsync();

        (int code, string stdout, string stderr) = await RunInProcess(s.Paths, "use", "nonsense");

        Assert.Equal(1, code);
        Assert.Equal("", stdout);
        Assert.Equal("ошибка: неизвестная стратегия\n", stderr);
    }

    [LinuxFact]
    public async Task OffAndOn()
    {
        await using Served s = await ServeAsync();

        (int offCode, string off, _) = await RunInProcess(s.Paths, "off");
        Assert.Equal(0, offCode);
        Assert.Contains("обход DPI: выключен\n", off);
        Assert.DoesNotContain("процесс:", off);

        (int onCode, string on, _) = await RunInProcess(s.Paths, "on");
        Assert.Equal(0, onCode);
        Assert.Contains("обход DPI: работает\n", on);
    }

    [LinuxFact]
    public async Task Strategies_MarksTheCurrentOne_AndTellsCustomFromBuiltIn()
    {
        await using Served s = await ServeAsync(rootOwned: ["mine"], beforeStart: p =>
        {
            Directory.CreateDirectory(p.StrategiesDir);
            File.WriteAllText(Path.Combine(p.StrategiesDir, "mine.args"), "--lua-desync=multisplit\n");
        });

        (int code, string stdout, _) = await RunInProcess(s.Paths, "strategies");

        Assert.Equal(0, code);
        string[] lines = stdout.TrimEnd('\n').Split('\n');
        Assert.Equal(6, lines.Length);
        Assert.Matches(@"^\* auto +встроенная$", lines[0]);
        Assert.Matches(@"^  split +встроенная$", lines[2]);
        Assert.Matches(@"^  custom:mine +своя$", lines[5]);
    }

    [LinuxFact]
    public async Task Exclusions_ListsHostsAndIps_OrSaysThereAreNone()
    {
        await using Served s = await ServeAsync();

        (_, string empty, _) = await RunInProcess(s.Paths, "exclusions");
        Assert.Equal("узлы: нет\nадреса: нет\n", empty);

        await s.Rig.Send(new ControlRequest(ControlCommands.SetExclusions, Hosts: ["a.example.com", "b.example.com"], Ips: ["192.0.2.0/24"]));
        (int code, string filled, _) = await RunInProcess(s.Paths, "exclusions");

        Assert.Equal(0, code);
        Assert.Equal("узлы (2):\n  a.example.com\n  b.example.com\nадреса (1):\n  192.0.2.0/24\n", filled);
    }

    [LinuxFact]
    public async Task NoService_ExitThree()
    {
        using var tmp = new TempDir();
        ServicePaths paths = ServicePaths.For(OsKind.Linux, n => n == "TGWS_SERVICE_ROOT" ? tmp.Path : null);

        (int code, string stdout, string stderr) = await RunInProcess(paths, "status");

        Assert.Equal(3, code);
        Assert.Equal("", stdout);
        Assert.Equal("служба обхода DPI недоступна\n", stderr);
    }

    // Arguments are '|'-separated so that "no arguments" and "an empty argument" can both be written down.
    [Theory]
    [InlineData("")]
    [InlineData("bogus")]
    [InlineData("use")]
    [InlineData("use|")]
    [InlineData("status|extra")]
    [InlineData("use|a|b")]
    [InlineData("STATUS")]
    public async Task BadUsage_ExitSixtyFour_WithTheUsageText(string joined)
    {
        string[] args = joined.Length == 0 ? [] : joined.Split('|');
        using var tmp = new TempDir();
        ServicePaths paths = ServicePaths.For(OsKind.Linux, n => n == "TGWS_SERVICE_ROOT" ? tmp.Path : null);

        (int code, string stdout, string stderr) = await RunInProcess(paths, args);

        Assert.Equal(64, code);   // decided before anyone is contacted: no service needed, and not code 3
        Assert.Equal("", stdout);
        Assert.Contains("Использование: tgwsone --zapret <команда>", stderr);
        Assert.Contains("use <id>", stderr);
    }

    [LinuxFact]
    public async Task RealExecutable_StatusUseAndExitCodes()
    {
        await using Served s = await ServeAsync();

        (int statusCode, string status, string _) = RunProcess(s.Root, "status");
        Assert.Equal(0, statusCode);
        Assert.Contains("обход DPI: работает", status);

        (int useCode, string use, _) = RunProcess(s.Root, "use", "aggressive");
        Assert.Equal(0, useCode);
        Assert.Contains("стратегия: aggressive", use);

        (int badCode, _, string badErr) = RunProcess(s.Root, "use", "nonsense");
        Assert.Equal(1, badCode);
        Assert.Contains("неизвестная стратегия", badErr);

        (int usageCode, _, string usageErr) = RunProcess(s.Root, "bogus");
        Assert.Equal(64, usageCode);
        Assert.Contains("Использование", usageErr);
    }

    [LinuxFact]
    public void RealExecutable_NoService_ExitThree()
    {
        using var tmp = new TempDir();

        (int code, _, string err) = RunProcess(tmp.Path, "status");

        Assert.Equal(3, code);
        Assert.Contains("служба обхода DPI недоступна", err);
    }

    [Fact]
    public void RealExecutable_UsageMentionsTheNewSwitches()
    {
        using var tmp = new TempDir();

        var psi = new ProcessStartInfo(Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") ?? "dotnet") { RedirectStandardOutput = true, UseShellExecute = false };
        psi.ArgumentList.Add(Path.Combine(AppContext.BaseDirectory, "TgwsOne.dll"));
        using Process p = Process.Start(psi)!;
        string o = p.StandardOutput.ReadToEnd();
        p.WaitForExit();

        Assert.Contains("--zapret", o);
        Assert.Contains("--service", o);
    }
}
