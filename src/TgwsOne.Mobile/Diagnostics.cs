using System.Globalization;
using System.Runtime.CompilerServices;
using TgwsOne.Engine;
using TgwsOne.Engine.Desync;

namespace TgwsOne.Mobile;

public enum CheckState { Ok, Warn, Fail, Skipped }

/// <summary>One line of the check list. <see cref="Detail"/> is an optional second line (why a check was skipped, say).</summary>
public sealed record CheckResult(string Title, CheckState State, string Detail = "");

/// <summary>What the button under the verdict does.</summary>
public enum VerdictAction { None, ChangePort, Connect, EnableBypass, FindStrategy, OpenInTelegram }

public sealed record Verdict(string Text, VerdictAction Action);

/// <summary>
/// «Проверить соединение» on the phone: the engine, the proxy, Telegram's attachment to it, the bypass, the
/// network and the engine's own full exchange. The checks run in the spec's order and come back one by one;
/// <see cref="Decide"/> then turns what the last run found into the verdict (the first matching row wins).
/// </summary>
public sealed class Diagnostics
{
    public static readonly TimeSpan ProbeTimeout = TimeSpan.FromSeconds(5);

    private static readonly CultureInfo Russian = CultureInfo.GetCultureInfo("ru-RU");

    private readonly IProxyEngineControl _proxy;
    private readonly IDesyncControl _desync;
    private readonly IEgressProbes _probes;
    private readonly Func<bool> _engineExists;
    private readonly Func<long> _activeSessions;
    private readonly string _host;

    private Facts? _facts;

    /// <param name="activeSessions">The engine's live client count: Android cannot read other apps' TCP table.</param>
    public Diagnostics(IProxyEngineControl proxy, IDesyncControl desync, IEgressProbes probes,
                       Func<bool> engineExists, Func<long> activeSessions, string host = "127.0.0.1")
    {
        _proxy = proxy;
        _desync = desync;
        _probes = probes;
        _engineExists = engineExists;
        _activeSessions = activeSessions;
        _host = host;
    }

    /// <summary>What a run found: everything the verdict is decided from.</summary>
    private sealed record Facts(bool EngineExists, EngineProxyStatus Proxy, long Connections, FinderBaseline Bypass,
                                int Tcp, int Tls, int BypassTls, bool CloudflareOk);

    private static int HostCount => DesyncFinder.TelegramHosts.Count;

    /// <summary>
    /// The eight checks, each as soon as it is known. The network ones are started together up front (so a run takes
    /// as long as the slowest of them, not the sum) and reported in order.
    /// </summary>
    public async IAsyncEnumerable<CheckResult> RunAsync([EnumeratorCancellation] CancellationToken ct)
    {
        _facts = null;

        bool engine = _engineExists();
        yield return engine
            ? new CheckResult("Движок прокси на месте", CheckState.Ok)
            : new CheckResult("Движок прокси не найден", CheckState.Fail);

        EngineProxyStatus proxy = _proxy.GetStatus();
        string address = $"{_host}:{proxy.Port}";
        yield return proxy.Listening ? new CheckResult($"Прокси слушает {address}", CheckState.Ok)
            : proxy.PortBusyByOther ? new CheckResult($"Порт {proxy.Port} занят другим приложением", CheckState.Fail)
            : proxy.Running ? new CheckResult("Прокси запущен, но порт ещё не слушается", CheckState.Warn)
            : new CheckResult("Прокси не запущен", CheckState.Fail);

        long connections = proxy.Listening ? _activeSessions() : 0;
        yield return connections > 0 ? new CheckResult("Telegram подключён к прокси", CheckState.Ok)
            : proxy.Listening ? new CheckResult("Telegram не подключён к прокси", CheckState.Warn)
            : new CheckResult("Подключение Telegram не проверено", CheckState.Skipped, "прокси не запущен");

        FinderBaseline bypass = await _desync.CaptureAsync(ct);
        yield return BypassCheck(bypass);

        Task<bool>[] tcp = [.. DesyncFinder.TelegramHosts.Select(h => _probes.TcpAsync(h, 443, ProbeTimeout, ct))];
        Task<TimeSpan?>[] tls = [.. DesyncFinder.TelegramHosts.Select(h => _probes.TlsAsync(h, 443, ProbeTimeout, ct))];
        Task<TimeSpan?>[] bypassTls = [.. DesyncFinder.TelegramHosts.Select(h =>
            _probes.BypassHandshakeAsync(h, 443, "ws", ProbeTimeout, ct))];
        Task<(bool Ok, TimeSpan Elapsed)> download = _probes.DownloadAsync(
            DesyncFinder.CloudflareUrl, DesyncFinder.CloudflareBytes, DesyncFinder.DownloadTimeout, ct);

        int tcpOk = (await Task.WhenAll(tcp)).Count(ok => ok);
        yield return Count("TCP до серверов Telegram", tcpOk);

        int tlsOk = (await Task.WhenAll(tls)).Count(t => t is not null);
        yield return tcpOk == 0
            ? new CheckResult("TLS до серверов Telegram не проверен", CheckState.Skipped, "до серверов нет даже TCP")
            : Count("TLS до серверов Telegram", tlsOk);

        int bypassTlsOk = (await Task.WhenAll(bypassTls)).Count(t => t is not null);
        yield return tcpOk == 0
            ? new CheckResult("TLS через обход не проверен", CheckState.Skipped, "до серверов нет даже TCP")
            : Count("TLS до серверов Telegram через обход", bypassTlsOk);

        (bool cloudflareOk, TimeSpan took) = await download;
        yield return cloudflareOk
            ? new CheckResult($"Cloudflare: 64 КБ за {took.TotalSeconds.ToString("0.0", Russian)} с", CheckState.Ok)
            : new CheckResult($"Cloudflare: 64 КБ не получены за {(int)DesyncFinder.DownloadTimeout.TotalSeconds} с", CheckState.Fail);

        _facts = new Facts(engine, proxy, connections, bypass, tcpOk, tlsOk, bypassTlsOk, cloudflareOk);
    }

    private static CheckResult BypassCheck(FinderBaseline bypass)
    {
        if (!bypass.Enabled) return new CheckResult("Обход: выключен", CheckState.Warn);
        string strategy = bypass.Strategy == DesyncFinder.AutoId
            ? "Авто"
            : BuiltInDesync.Find(bypass.Strategy)?.Title ?? bypass.Strategy;
        return new CheckResult($"Обход: включён, {strategy}", CheckState.Ok);
    }

    private static CheckResult Count(string what, int ok) => new($"{what}: {ok}/{HostCount}",
        ok == HostCount ? CheckState.Ok : ok == 0 ? CheckState.Fail : CheckState.Warn);

    /// <summary>The spec's verdict table for what the last run found, first matching row.</summary>
    /// <exception cref="InvalidOperationException">No run has finished yet.</exception>
    public Verdict Decide()
    {
        Facts f = _facts ?? throw new InvalidOperationException("сначала нужна проверка");

        if (!f.EngineExists)
            return new("Повреждена установка: нет движка прокси. Переустановите TgwsOne.", VerdictAction.None);
        if (!f.Proxy.Listening && f.Proxy.PortBusyByOther)
            return new($"Порт {f.Proxy.Port} занят другим приложением.", VerdictAction.ChangePort);
        if (!f.Proxy.Listening)
            return new("Прокси выключен.", VerdictAction.Connect);
        if (f.Tcp == 0)
            return new("Нет связи с серверами Telegram — проверьте интернет; если не поможет, нужен VPN.", VerdictAction.None);
        if (f.Tls < HostCount)
        {
            if (!f.Bypass.Enabled)
                return new("TLS режется, обход выключен.", VerdictAction.EnableBypass);
            if (f.BypassTls < HostCount)
                return new("TLS режется, текущая стратегия не помогает.", VerdictAction.FindStrategy);
        }
        if (!f.CloudflareOk)
            return new("Cloudflare работает с перебоями — резервный канал прокси может тормозить.", VerdictAction.FindStrategy);
        if (f.Connections == 0)
            return new("Прокси работает, но Telegram к нему не подключён.", VerdictAction.OpenInTelegram);
        return new("Всё в порядке.", VerdictAction.None);
    }
}
