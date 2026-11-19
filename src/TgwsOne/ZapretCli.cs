using System.Globalization;
using TgwsOne.Core.Zapret;

namespace TgwsOne;

/// <summary>
/// <c>tgwsone --zapret …</c>: talks to the bypass service over its control channel and prints a short summary.
/// Exit codes: 0 done, 1 the service refused (its message goes to stderr), 3 no service to talk to, 64 bad usage.
/// </summary>
internal static class ZapretCli
{
    public const int ExitError = 1;
    public const int ExitUnavailable = 3;
    public const int ExitUsage = 64;   // EX_USAGE, as in Program

    public const string Usage = """
        Использование: tgwsone --zapret <команда>
          status       показать состояние обхода DPI
          on           включить
          off          выключить
          use <id>     выбрать стратегию (список — strategies)
          strategies   показать доступные стратегии
          exclusions   показать исключения (узлы и адреса, которые обход не трогает)
        """;

    public static async Task<int> RunAsync(string[] args, ServicePaths paths, TextWriter stdout, TextWriter stderr, TimeSpan? timeout = null)
    {
        ControlRequest? request = Parse(args);
        if (request == null)
        {
            stderr.WriteLine(args.Length == 0 ? "не указана команда" : $"неизвестная команда: {string.Join(' ', args)}");
            stderr.WriteLine(Usage);
            return ExitUsage;
        }

        ControlResponse response;
        try
        {
            response = await new ControlClient(paths, timeout).SendAsync(request);
        }
        catch (ServiceUnavailableException ex)
        {
            stderr.WriteLine(ex.Message);
            return ExitUnavailable;
        }

        if (!response.Ok)
        {
            stderr.WriteLine($"ошибка: {response.Error}");
            return ExitError;
        }
        Print(request.Cmd, response, stdout);
        return 0;
    }

    private static ControlRequest? Parse(string[] args) => args switch
    {
        ["status"] => new ControlRequest(ControlCommands.Status),
        ["on"] => new ControlRequest(ControlCommands.On),
        ["off"] => new ControlRequest(ControlCommands.Off),
        ["use", var id] when id.Length > 0 => new ControlRequest(ControlCommands.Use, Strategy: id),
        ["strategies"] => new ControlRequest(ControlCommands.Strategies),
        ["exclusions"] => new ControlRequest(ControlCommands.Exclusions),
        _ => null,
    };

    private static void Print(string cmd, ControlResponse r, TextWriter w)
    {
        switch (cmd)
        {
            case ControlCommands.Strategies:
                foreach (StrategyItem item in r.Items ?? [])
                    w.WriteLine($"{(item.Id == r.Current ? '*' : ' ')} {item.Id,-22} {(item.Kind == "custom" ? "своя" : "встроенная")}");
                break;
            case ControlCommands.Exclusions:
                PrintList(w, "узлы", r.Hosts);
                PrintList(w, "адреса", r.Ips);
                break;
            default:
                if (r.Status != null) PrintStatus(r.Status, w);
                break;
        }
    }

    private static void PrintList(TextWriter w, string title, IReadOnlyList<string>? items)
    {
        if (items == null || items.Count == 0)
        {
            w.WriteLine($"{title}: нет");
            return;
        }
        w.WriteLine($"{title} ({items.Count}):");
        foreach (string item in items) w.WriteLine($"  {item}");
    }

    private static void PrintStatus(ZapretStatus s, TextWriter w)
    {
        w.WriteLine($"обход DPI: {StateText(s.State)}");
        w.WriteLine($"стратегия: {s.Strategy}");
        if (s.Pid is { } pid)
        {
            string since = s.Since is { } t ? $", с {t.ToLocalTime().ToString("HH:mm:ss", CultureInfo.InvariantCulture)}" : "";
            w.WriteLine($"процесс: {pid}{since}");
        }
        if (s.Restarts > 0) w.WriteLine($"перезапусков: {s.Restarts}");
        if (!string.IsNullOrEmpty(s.LastError))
        {
            w.WriteLine("последняя ошибка:");
            foreach (string line in s.LastError.Split('\n')) w.WriteLine($"  {line.TrimEnd('\r')}");
        }
        w.WriteLine($"пакет zapret2: {s.Bundle}");
        if (s.Auto is { Count: > 0 } auto)
        {
            w.WriteLine("авто, по хостам:");
            foreach (AutoHost h in auto) w.WriteLine($"  {h.Host}  {h.Strategy}  (неудач: {h.Failures})");
        }
    }

    private static string StateText(string state) => state switch
    {
        "disabled" => "выключен",
        "starting" => "запускается",
        "running" => "работает",
        "backoff" => "перезапуск после сбоя",
        "error" => "ошибка",
        _ => state,
    };
}
