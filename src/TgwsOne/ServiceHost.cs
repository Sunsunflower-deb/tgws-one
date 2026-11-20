using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using TgwsOne.Core;
using TgwsOne.Core.Zapret;
using TgwsOne.Core.Zapret.Windows;

namespace TgwsOne;

/// <summary>
/// <c>tgwsone --service</c>: the privileged half. A Generic Host, so the same code runs as a Windows service
/// (LocalSystem, name <see cref="WindowsServiceName"/>), as a systemd daemon (Type=notify) and, started by hand, as a
/// plain console process that stops on Ctrl+C / SIGTERM.
/// </summary>
internal static class ServiceHost
{
    public const string WindowsServiceName = "TgwsOneZapret";

    public static async Task<int> RunAsync()
    {
        // No command-line configuration, and no probing of whatever directory the service happened to start in.
        var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings
        {
            Args = [],
            ContentRootPath = AppContext.BaseDirectory,
        });
        if (OperatingSystem.IsWindows()) builder.Services.AddWindowsService(o => o.ServiceName = WindowsServiceName);
        builder.Services.AddSystemd();
        builder.Services.AddHostedService<ZapretWorker>();

        using IHost host = builder.Build();
        await host.RunAsync();
        return 0;
    }

    /// <summary>Owns the bypass for as long as the host runs: everything is built here and torn down in the reverse order.</summary>
    private sealed class ZapretWorker : BackgroundService
    {
        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            OsKind os = OperatingSystem.IsWindows() ? OsKind.Windows : OsKind.Linux;
            Func<string, string?> env = Environment.GetEnvironmentVariable;
            ServicePaths paths = ServicePaths.For(os, env);
            string appDir = AppContext.BaseDirectory;
            var log = new AppLog(Path.Combine(paths.LogsDir, "service.log"), publicRead: true);   // the app's Журналы page reads it

            try
            {
                if (OperatingSystem.IsWindows())
                {
                    // Not optional: without it any user could plant a strategy that runs as SYSTEM.
                    ServiceAcl.Protect(paths.WindowsRoot!);
                    await LegacyMigration.RunAsync(new ProcessCommandRunner(), appDir, paths.WindowsRoot!, log: log);
                }

                ZapretBundle bundle = ZapretBundle.Locate(os, appDir, env);
                NftManager? nft = os == OsKind.Linux ? new NftManager(new ProcessCommandRunner(), new ProcSysctl()) : null;
                if (nft != null && env("TGWS_NO_NFT") == "1")
                {
                    // Test rigs only (the UI's visual checks run a user-level service with FakeZapret, which cannot load nftables rules).
                    log.Write("предупреждение: TGWS_NO_NFT=1 — правила nftables не применяются, перехват трафика не работает");
                    nft = null;
                }
                var runner = new ZapretRunner(bundle.Exe, Path.Combine(paths.LogsDir, "zapret.log"), log, nft);
                string? debugFile = env("TGWS_ZAPRET_DEBUG") is { Length: > 0 } d ? d : null;
                await using var service = new ZapretService(os, paths, bundle, Path.Combine(appDir, "lists"), runner, log, debugFile: debugFile);

                await service.StartAsync(stoppingToken);
                log.Write($"служба: запущена, канал управления {paths.Endpoint}");
                await new ControlServer(paths, service, log).RunAsync(stoppingToken);   // until the host stops us
                log.Write("служба: остановка");
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                log.Write($"служба: аварийная остановка: {ex}");
                throw;
            }
        }
    }
}
