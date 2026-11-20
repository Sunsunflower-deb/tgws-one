using System.Globalization;
using System.Runtime.CompilerServices;
using TgwsOne.Core;
using TgwsOne.Core.Zapret;

namespace TgwsOne.Services;

internal enum CheckState { Ok, Warn, Fail, Skipped }

/// <summary>One line of the check list. <see cref="Detail"/> is an optional second line (why a check was skipped, say).</summary>
internal sealed record CheckResult(string Title, CheckState State, string Detail = "");

/// <summary>What the button under the verdict does.</summary>
internal enum VerdictAction { None, ChangePort, Connect, EnableBypass, FindStrategy, OpenInTelegram }

internal sealed record Verdict(string Text, VerdictAction Action);

/// <summary>
/// «Проверить соединение»: looks at the proxy, the bypass and the network and says in one sentence what is wrong and what to do
/// about it. The seven checks run in the order of the spec and come back one by one; <see cref="Decide"/> then turns what the
/// last run found into the verdict (the first matching row of the spec's table wins).
/// </summary>
internal sealed class Diagnostics
{
    public static readonly TimeSpan ProbeTimeout = TimeSpan.FromSeconds(5);

    private static readonly CultureInfo Russian = CultureInfo.GetCultureInfo("ru-RU");

    private readonly IProxyControl _proxy;
    private readonly IControlClient _control;
    private readonly INetInfo _net;
    private readonly IAppSettings _settings;
    private readonly ITcpProbe _tcp;
    private readonly ITlsProbe _tls;
    private readonly IHttpProbe _http;
    private readonly Func<bool> _engineExists;

    private Facts? _facts;

    public Diagnostics(IProxyControl proxy, IControlClient control, INetInfo net, IAppSettings settings,
                       ITcpProbe tcp, ITlsProbe tls, IHttpProbe http, Func<bool> engineExists)
    {
        _proxy = proxy;
        _control = control;
        _net = net;
        _settings = settings;
        _tcp = tcp;
        _tls = tls;
        _http = http;
        _engineExists = engineExists;
    }

    /// <summary>What a run found: everything the verdict is decided from.</summary>
    private sealed record Facts(bool EngineExists, ProxyStatus Proxy, int Connections, bool ServiceReachable, ZapretStatus? Bypass,
                                int Tcp, int Tls, bool CloudflareOk);

    private static int HostCount => StrategyFinder.TelegramHosts.Count;

    /// <summary>
    /// The seven checks, each as soon as it is known. The network ones are started together up front (so a run takes as long as
    /// the slowest of them, not the sum) and reported in order.
    /// </summary>
    public async IAsyncEnumerable<CheckResult> RunAsync([EnumeratorCancellation] CancellationToken ct)
    {
        _facts = null;

        bool engine = _engineExists();
        yield return engine
            ? new CheckResult("Движок прокси на месте", CheckState.Ok)
            : new CheckResult("Движок прокси не найден", CheckState.Fail);

        ProxyStatus proxy = await Task.Run(_proxy.GetStatus, ct);
        string address = $"{_settings.Load().Host}:{proxy.Port}";
        yield return proxy.Listening ? new CheckResult($"Прокси слушает {address}", CheckState.Ok)
            : proxy.PortBusyByOther ? new CheckResult($"Порт {proxy.Port} занят другим приложением", CheckState.Fail)
            : proxy.Running ? new CheckResult("Прокси запущен, но порт ещё не слушается", CheckState.Warn)
            : new CheckResult("Прокси не запущен", CheckState.Fail);

        int connections = proxy.Listening ? TelegramConnections.Count(proxy.Port, _net) : 0;
        yield return connections > 0 ? new CheckResult("Telegram подключён к прокси", CheckState.Ok)
            : proxy.Listening ? new CheckResult("Telegram не подключён к прокси", CheckState.Warn)
            : new CheckResult("Подключение Telegram не проверено", CheckState.Skipped, "прокси не запущен");

        (bool reachable, ZapretStatus? bypass) = await ServiceAsync(ct);
        yield return BypassCheck(reachable, bypass);

        Task<bool>[] tcp = [.. StrategyFinder.TelegramHosts.Select(h => _tcp.ConnectAsync(h, 443, ProbeTimeout, ct))];
        Task<TimeSpan?>[] tls = [.. StrategyFinder.TelegramHosts.Select(h => _tls.HandshakeAsync(h, 443, ProbeTimeout, ct))];
        Task<(bool Ok, TimeSpan Elapsed)> download = _http.DownloadAsync(StrategyFinder.CloudflareUrl, StrategyFinder.CloudflareBytes, StrategyFinder.DownloadTimeout, ct);

        int tcpOk = (await Task.WhenAll(tcp)).Count(ok => ok);
        yield return Count("TCP до серверов Telegram", tcpOk);

        int tlsOk = (await Task.WhenAll(tls)).Count(t => t is not null);
        yield return tcpOk == 0
            ? new CheckResult("TLS до серверов Telegram не проверен", CheckState.Skipped, "до серверов нет даже TCP")
            : Count("TLS до серверов Telegram", tlsOk);

        (bool cloudflareOk, TimeSpan took) = await download;
        yield return cloudflareOk
            ? new CheckResult($"Cloudflare: 64 КБ за {took.TotalSeconds.ToString("0.0", Russian)} с", CheckState.Ok)
            : new CheckResult($"Cloudflare: 64 КБ не получены за {(int)StrategyFinder.DownloadTimeout.TotalSeconds} с", CheckState.Fail);

        _facts = new Facts(engine, proxy, connections, reachable, bypass, tcpOk, tlsOk, cloudflareOk);
    }

    private async Task<(bool Reachable, ZapretStatus? Status)> ServiceAsync(CancellationToken ct)
    {
        try
        {
            ControlResponse r = await _control.SendAsync(new ControlRequest(ControlCommands.Status), ct);
            return (true, r.Status);
        }
        catch (ServiceUnavailableException)
        {
            return (false, null);
        }
    }

    private static CheckResult BypassCheck(bool reachable, ZapretStatus? bypass)
    {
        if (!reachable || bypass is null) return new CheckResult("Служба обхода: не установлена", CheckState.Warn);
        return bypass.State switch
        {
            "disabled" => new CheckResult("Служба обхода: выключена", CheckState.Warn),
            "error" => new CheckResult($"Служба обхода: ошибка — {FirstLine(bypass.LastError)}", CheckState.Fail),
            "starting" or "backoff" => new CheckResult("Служба обхода: запускается", CheckState.Warn),
            _ => new CheckResult($"Служба обхода: включена, {(bypass.Strategy == BuiltInStrategies.Auto ? "Авто" : StatusText.DisplayName(bypass.Strategy))}", CheckState.Ok),
        };
    }

    private static string FirstLine(string? text) => (text ?? "").Split('\n', 2)[0].Trim() is { Length: > 0 } line ? line : "без подробностей";

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
            if (!f.ServiceReachable)
                return new("Провайдер режет Telegram, а обход DPI не установлен — попросите администратора переустановить TgwsOne.", VerdictAction.None);
            if (f.Bypass is not { Enabled: true })
                return new("Провайдер режет Telegram. Включите обход DPI.", VerdictAction.EnableBypass);
            return new("Провайдер режет Telegram, обход не справляется.", VerdictAction.FindStrategy);
        }
        if (!f.CloudflareOk)
            return new("Cloudflare работает с перебоями — резервный канал прокси может тормозить.", VerdictAction.FindStrategy);
        if (f.Connections == 0)
            return new("Прокси работает, но Telegram к нему не подключён.", VerdictAction.OpenInTelegram);
        return new("Всё в порядке.", VerdictAction.None);
    }
}
