using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Avalonia;
using TgwsOne.Core;
using TgwsOne.Core.Zapret;
using TgwsOne.Services;

namespace TgwsOne;

// The window, or the CLI. Main looks at the arguments first and only a launch without switches starts Avalonia, so
// scripts, the service and every --switch behave exactly as before and never load the UI libraries.
internal static class Program
{
    private const int ExitUsage = 64;   // EX_USAGE
    private const int AttachParentProcess = -1;   // ATTACH_PARENT_PROCESS

    private static string Version => typeof(Program).Assembly.GetName().Version!.ToString(3);

    private static string Usage => $"""
        TgwsOne {Version} — прокси для Telegram

        Использование: tgwsone [<ключ>]
          (без ключей) открыть окно приложения
          --tray       запуститься в трее: подключиться и скрыть окно (так стартует автозапуск)
          --start      запустить прокси
          --stop       остановить прокси
          --status     показать состояние (код 0 — слушает, 1 — нет)
          --selftest   проверить окружение (код — число найденных проблем)
          --version    показать версию
          --zapret     управлять обходом DPI через службу: --zapret status|on|off|use <id>|strategies|exclusions
          --service    запустить службу обхода DPI (для systemd / службы Windows)
          --admin      служебное: сохранить или удалить свою стратегию (запускается из окна с правами администратора)
        """;

    /// <summary>No arguments, or just <c>--tray</c>: the window. Everything else is the CLI.</summary>
    internal static bool IsUiLaunch(string[] args) => args.Length == 0 || args is ["--tray"];

    [STAThread]
    private static int Main(string[] args)
    {
        if (IsUiLaunch(args)) return RunUi(args);

        // The Windows exe is a GUI one: without this a command typed in a console prints nothing.
        if (OperatingSystem.IsWindows()) AttachParentConsole();
        return RunCliAsync(args).GetAwaiter().GetResult();
    }

    // Not inlined: keeps the Avalonia types out of Main, so the CLI paths never touch the UI assemblies.
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static int RunUi(string[] args) => BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);

    /// <summary>Also what the previewer and the headless tests configure the app from.</summary>
    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<App>().UsePlatformDetect().WithInterFont().LogToTrace();

    [SupportedOSPlatform("windows")]
    private static void AttachParentConsole() => AttachConsole(AttachParentProcess);

    [DllImport("kernel32.dll")]
    private static extern bool AttachConsole(int processId);

    private static async Task<int> RunCliAsync(string[] args)
    {
        if (args[0] == "--zapret")
            return await ZapretCli.RunAsync(args[1..], ServicePaths.Current, Console.Out, Console.Error);

        // Started by the window with administrator rights (pkexec, UAC) to save or delete a custom strategy; see AdminCli.
        if (args[0] == "--admin")
            return AdminCli.Run(args[1..], ServicePaths.Current, OperatingSystem.IsWindows() ? OsKind.Windows : OsKind.Linux, Console.Out, Console.Error);

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
