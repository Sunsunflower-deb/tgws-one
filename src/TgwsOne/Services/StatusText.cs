using TgwsOne.Core;
using TgwsOne.Core.Zapret;

namespace TgwsOne.Services;

/// <summary>The words and colours of the status model (spec: "Status model"): one pure function per line of the Home page.</summary>
internal static class StatusText
{
    private const string TelegramHostSuffix = ".web.telegram.org";

    /// <summary>The headline at the top of Главная, the tray tooltip and the tray icon colour; the first matching row wins.</summary>
    public static (string Text, Tone Tone) Headline(AppSnapshot s)
    {
        // A failed start is only the headline while nothing listens: a proxy that came up since (started from the CLI, say)
        // is a running proxy, whatever the last attempt from this window said.
        if (s.LastStart is { Ok: false } failed && !s.Proxy.Listening)
            return ($"Прокси не запустился: {StartFailure(failed)}", Tone.Red);
        if (s.Proxy.Listening)
            return s.TelegramConnections >= 1
                ? ("Telegram работает через прокси", Tone.Green)
                : ("Прокси готов — откройте ссылку в Telegram", Tone.Amber);
        return ("Отключено", Tone.Grey);
    }

    /// <summary>
    /// The bypass line. The chevron after the Авто summary that opens Обход DPI is drawn by the view: it is not text.
    /// </summary>
    public static (string Text, Tone Tone) BypassLine(AppSnapshot s)
    {
        if (!s.ServiceReachable) return ("не установлен — нужен администратор", Tone.Grey);
        if (s.Bypass is not { } b) return ("нет данных", Tone.Grey);

        return b.State switch
        {
            "disabled" => ("выключен", Tone.Grey),
            "starting" => ("запускается…", Tone.Amber),
            "backoff" => ("перезапуск после сбоя…", Tone.Amber),
            "error" => (ErrorLine(b.LastError), Tone.Red),
            "running" => (RunningLine(b), Tone.Green),
            _ => ($"состояние: {b.State}", Tone.Grey),
        };
    }

    /// <summary>The name shown for a strategy id: the built-ins in Russian, <c>custom:x</c> as «x (своя)».</summary>
    public static string DisplayName(string strategyId) => strategyId switch
    {
        BuiltInStrategies.Auto => "Авто (рекомендуется)",
        BuiltInStrategies.FakeTls => "Фейковый TLS",
        BuiltInStrategies.Split => "Разрезание",
        BuiltInStrategies.SplitStealth => "Разрезание (скрытное)",
        BuiltInStrategies.Aggressive => "Агрессивная",
        _ when strategyId.StartsWith("custom:", StringComparison.Ordinal) && strategyId.Length > "custom:".Length
            => $"{strategyId["custom:".Length..]} (своя)",
        _ => strategyId,
    };

    /// <summary>Why a start failed, in a phrase that follows «Прокси не запустился: ».</summary>
    public static string StartFailure(StartResult r) => r.Outcome switch
    {
        StartOutcome.PortExhausted => $"порт {r.Port} занят, свободных портов рядом нет",
        StartOutcome.EngineMissing => "не найден движок прокси",
        StartOutcome.EngineExited when r.ExitCode is { } code => $"движок завершился с кодом {code}",
        StartOutcome.EngineExited => "движок не запустился",
        StartOutcome.Timeout => $"порт {r.Port} не слушается",
        _ => r.Outcome.ToString(),
    };

    private static string ErrorLine(string? lastError)
    {
        string first = (lastError ?? "").Split('\n', 2)[0].Trim();
        return first.Length == 0 ? "ошибка" : $"ошибка: {first}";
    }

    private static string RunningLine(ZapretStatus b)
    {
        if (b.Strategy != BuiltInStrategies.Auto) return $"включён · {DisplayName(b.Strategy)}";

        string line = "включён · Авто";
        if (b.Auto is not { Count: > 0 } auto) return line;
        line += $" · {ShortHost(auto[0].Host)} → {auto[0].Strategy}";
        return auto.Count > 1 ? line + $", ещё {auto.Count - 1}" : line;
    }

    // kws2.web.telegram.org → kws2; any other host is shown whole.
    private static string ShortHost(string host)
        => host.EndsWith(TelegramHostSuffix, StringComparison.Ordinal) ? host[..^TelegramHostSuffix.Length] : host;
}
