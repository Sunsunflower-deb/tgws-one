using TgwsOne.Core.Zapret;

namespace TgwsOne.Core.Tests.Zapret;

/// <summary>
/// A <see cref="ZapretService"/> over a temp <c>TGWS_SERVICE_ROOT</c>, with FakeZapret as the executable and no nft.
/// Custom strategies named in <c>rootOwned</c> pass the ownership check, any other file looks like it belongs to uid 1000.
/// </summary>
internal sealed class ServiceRig : IAsyncDisposable
{
    private ServiceRig(ZapretService service, ServicePaths paths, ZapretBundle bundle, ZapretRunner runner, AppLog log, string appLists, TempDir tmp)
    {
        Service = service;
        Paths = paths;
        Bundle = bundle;
        Runner = runner;
        Log = log;
        AppLists = appLists;
        Tmp = tmp;
    }

    public ZapretService Service { get; }
    public ServicePaths Paths { get; }
    public ZapretBundle Bundle { get; }
    public ZapretRunner Runner { get; }
    public AppLog Log { get; }
    public string AppLists { get; }
    public TempDir Tmp { get; }

    /// <param name="beforeStart">Runs after the paths are known and before the service starts (to pre-write state, lists, …).</param>
    /// <param name="start">False to create the service without starting it.</param>
    public static async Task<ServiceRig> StartAsync(FakeZapretEnv env, Action<ServicePaths>? beforeStart = null, string[]? rootOwned = null,
                                                    bool bundleMissing = false, bool start = true)
    {
        var tmp = new TempDir();
        string root = tmp.Combine("root");
        string appLists = tmp.Combine("app-lists");
        Directory.CreateDirectory(appLists);
        File.WriteAllText(Path.Combine(appLists, "telegram.txt"), "# shipped\ntelegram.org\n");
        File.WriteAllText(Path.Combine(appLists, "cloudflare-ips.txt"), "# shipped\n173.245.48.0/20\n");

        ServicePaths paths = ServicePaths.For(OsKind.Linux, n => n == "TGWS_SERVICE_ROOT" ? root : null);
        ZapretBundle bundle = ZapretBundle.Locate(OsKind.Linux, tmp.Path, n => n switch
        {
            "TGWS_ZAPRET_DIR" => tmp.Combine("bundle"),
            "TGWS_ZAPRET_EXE" => bundleMissing ? tmp.Combine("no-such-nfqws2") : FakeZapretEnv.ExePath,
            _ => null,
        });
        beforeStart?.Invoke(paths);

        UnixFileMeta? Stat(string path)
            => rootOwned is { } owned && owned.Contains(Path.GetFileNameWithoutExtension(path)) ? new UnixFileMeta(0, 0x1A4) : new UnixFileMeta(1000, 0x1A4);

        var log = new AppLog(env.ServiceLog);
        var runner = new ZapretRunner(bundle.Exe, env.LogFile, log, nft: null, FakeZapretEnv.FastPolicy);
        var service = new ZapretService(OsKind.Linux, paths, bundle, appLists, runner, log, Stat);
        var rig = new ServiceRig(service, paths, bundle, runner, log, appLists, tmp);
        if (start) await service.StartAsync();
        return rig;
    }

    public Task<ControlResponse> Send(ControlRequest request) => Service.HandleAsync(request, CancellationToken.None);
    public Task<ControlResponse> Send(string cmd, string? strategy = null) => Send(new ControlRequest(cmd, Strategy: strategy));

    public string[] ExpectedArgv(string id, DesyncPart? custom = null)
        => [.. ZapretArgs.Build(OsKind.Linux, Bundle, Paths.Lists, id, Paths.StateDir, custom)];

    public async ValueTask DisposeAsync()
    {
        await Service.DisposeAsync();
        Tmp.Dispose();
    }
}
