using System.Reflection;
using TgwsOne.Core;
using TgwsOne.Core.Zapret;

namespace TgwsOne;

// The CLI. In SP3 this project becomes the Avalonia app; Main will keep dispatching these switches
// before any UI starts, so scripts and the service keep working unchanged.
internal static class Program
{
    private const int ExitUsage = 64;   // EX_USAGE

    private static string Version => typeof(Program).Assembly.GetName().Version!.ToString(3);

    private static string Usage => $"""
        TgwsOne {Version} — прокси для Telegram

        Использование: tgwsone <ключ>
          --start      запустить прокси
          --stop       остановить прокси
          --status     показать состояние (код 0 — слушает, 1 — нет)
          --selftest   проверить окружение (код — число найденных проблем)
          --version    показать версию
          --zapret     управлять обходом DPI через службу: --zapret status|on|off|use <id>|strategies|exclusions
          --service    запустить службу обхода DPI (для systemd / службы Windows)
        """;

    private static async Task<int> Main(string[] args)
    {
        if (args.Length == 0)
        {
            Console.WriteLine(Usage);
            return 0;
        }

        if (args[0] == "--zapret")
            return await ZapretCli.RunAsync(args[1..], ServicePaths.Current, Console.Out, Console.Error);

        switch (args.Length == 1 ? args[0] : "")
        {
            case "--service": return await ServiceHost.RunAsync();
            case "--start": return await StartAsync();
            case "--stop": return await StopAsync();
            case "--status": return Status();
            case "--selftest": return SelfTest.Run(AppPaths.Current, Console.Out);
            case "--version":
                Console.WriteLine($"TgwsOne {Version}");
                return 0;
            case "--help" or "-h":
                Console.WriteLine(Usage);
                return 0;
            default:
                Console.Error.WriteLine($"неизвестные аргументы: {string.Join(' ', args)}");
                Console.Error.WriteLine(Usage);
                return ExitUsage;
        }
    }

    private static ProxyController NewController(AppPaths paths) => new(paths, new AppLog(paths.AppLogFile));

    private static async Task<int> StartAsync()
    {
        AppPaths paths = AppPaths.Current;
        try
        {
            StartResult r = await NewController(paths).StartAsync();
            if (r.Ok)
            {
                Console.WriteLine(r.Outcome switch
                {
                    StartOutcome.AlreadyRunning => $"уже запущен (порт {r.Port})",
                    StartOutcome.PortSwitched => $"порт {r.OldPort} занят — запущен на порту {r.Port}",
                    _ => $"запущен (порт {r.Port})",
                });
                return 0;
            }

            Console.Error.WriteLine("не удалось запустить: " + r.Outcome switch
            {
                StartOutcome.PortExhausted => $"порт {r.Port} занят, свободных портов рядом нет",
                StartOutcome.EngineMissing => $"движок не найден: {paths.Engine}",
                StartOutcome.EngineExited when r.ExitCode is { } code => $"движок завершился с кодом {code}",
                StartOutcome.EngineExited => "движок не запустился",
                StartOutcome.Timeout => $"порт {r.Port} не слушается (подробности в {paths.ProxyLogFile})",
                _ => r.Outcome.ToString(),
            });
            if (r.Output.Length > 0)
                foreach (string line in r.Output.Split('\n'))
                    Console.Error.WriteLine("  " + line);
            return 2;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Console.Error.WriteLine("не удалось запустить: " + ex.Message);
            return 2;
        }
    }

    private static async Task<int> StopAsync()
    {
        AppPaths paths = AppPaths.Current;
        try
        {
            StopOutcome r = await NewController(paths).StopAsync();
            Console.WriteLine(r == StopOutcome.Stopped ? "остановлен" : "не был запущен");
            return 0;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Console.Error.WriteLine("не удалось остановить: " + ex.Message);
            return 1;
        }
    }

    private static int Status()
    {
        AppPaths paths = AppPaths.Current;
        try
        {
            ProxyStatus st = NewController(paths).GetStatus();
            Console.WriteLine($"running={Flag(st.Running)} listening={Flag(st.Listening)} port={st.Port}");
            return st.Listening ? 0 : 1;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine("не удалось получить состояние: " + ex.Message);
            return 1;
        }
    }

    private static string Flag(bool b) => b ? "true" : "false";
}
