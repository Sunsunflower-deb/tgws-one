using TgwsOne.Engine;
using TgwsOne.Engine.Desync;

namespace TgwsOne.Mobile;

/// <summary>The colour a status is shown in on the phone.</summary>
public enum Tone { Green, Amber, Grey, Red }

/// <summary>
/// The words of the mobile status lines, taken from the desktop status model so both builds say the same things.
/// The desktop helpers read the zapret service snapshot; the phone has no such service, so the same phrases are
/// produced from the engine's own status here.
/// </summary>
public static class MobileStatusText
{
    public const string Auto = "Авто";
    public const string NoBypass = "Без обхода";

    private const string TelegramHostSuffix = ".web.telegram.org";

    /// <summary>The one phrase at the top of Главная: the first matching row wins.</summary>
    public static (string Text, Tone Tone) HomeLine(EngineProxyStatus status, EngineStartResult? lastStart, long connections)
    {
        if (lastStart is { Ok: false } failed && !status.Listening)
            return ($"Прокси не запустился: {StartFailure(failed)}", Tone.Red);
        if (status.Listening)
            return connections >= 1
                ? ("Telegram работает через прокси", Tone.Green)
                : ("Прокси готов — откройте ссылку в Telegram", Tone.Amber);
        return ("Отключено", Tone.Grey);
    }

    /// <summary>The proxy's own line: what it is doing and where it listens.</summary>
    public static (string Text, Tone Tone) ProxyLine(EngineProxyStatus p, string host)
    {
        if (p.Listening) return ($"работает · {host}:{p.Port}", Tone.Green);
        if (p.Running) return ("запускается…", Tone.Amber);
        if (p.PortBusyByOther) return ($"остановлен · порт {p.Port} занят другим приложением", Tone.Amber);
        return ("остановлен", Tone.Grey);
    }

    /// <summary>The bypass line; <paramref name="auto"/> is what Авто has chosen per host so far.</summary>
    public static (string Text, Tone Tone) BypassLine(bool enabled, string strategy, IReadOnlyList<AutoHost> auto)
    {
        if (!enabled) return ("выключен", Tone.Grey);
        if (strategy != DesyncFinder.AutoId) return ($"включён · {StrategyName(strategy)}", Tone.Green);

        string line = "включён · Авто";
        if (auto.Count == 0) return (line, Tone.Green);
        line += $" · {ShortHost(auto[0].Host)} → {StrategyName(auto[0].Strategy)}";
        return auto.Count > 1 ? (line + $", ещё {auto.Count - 1}", Tone.Green) : (line, Tone.Green);
    }

    /// <summary>
    /// The name shown for a candidate. Built-in strategies keep the Russian titles that travel with the engine;
    /// a candidate with a fake-TTL override (<c>id@ttl</c>) says which TTL was measured.
    /// </summary>
    public static string StrategyName(string id)
    {
        if (id == DesyncFinder.AutoId) return Auto;
        if (id == DesyncFinder.NoBypassId) return NoBypass;

        DesyncFinder.TryParseCandidate(id, out string baseId, out int? ttl);
        string name = BuiltInDesync.Find(baseId)?.Title ?? baseId;
        return ttl is { } value ? $"{name} · TTL {value}" : name;
    }

    /// <summary>Why a start failed, in a phrase that follows «Прокси не запустился: ».</summary>
    public static string StartFailure(EngineStartResult r) => r.Outcome switch
    {
        EngineStartOutcome.PortExhausted => $"порт {r.Port} занят, свободных портов рядом нет",
        EngineStartOutcome.EngineMissing => "не найден движок прокси",
        EngineStartOutcome.EngineExited when r.ExitCode is { } code => $"движок завершился с кодом {code}",
        EngineStartOutcome.EngineExited => "движок не запустился",
        EngineStartOutcome.Timeout => $"порт {r.Port} не слушается",
        _ => r.Outcome.ToString(),
    };

    // kws2.web.telegram.org → kws2; any other host is shown whole.
    private static string ShortHost(string host)
        => host.EndsWith(TelegramHostSuffix, StringComparison.Ordinal) ? host[..^TelegramHostSuffix.Length] : host;
}
