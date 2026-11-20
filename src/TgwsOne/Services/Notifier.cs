using TgwsOne.Core;

namespace TgwsOne.Services;

/// <summary>One kind of notification: the throttle counts each kind separately.</summary>
internal enum NoticeKind
{
    ProxyDied,
    ProxyRestarted,
    BypassError,
    BypassRecovered,
    PortSwitched,
    StartFailed,
    TrayHint,
}

/// <summary>Where a notification goes: the desktop's own notification area. Best effort: a missing daemon is not an error.</summary>
internal interface INotificationSink
{
    Task ShowAsync(string title, string body, CancellationToken ct = default);
}

/// <summary>
/// Tells the user what happened while the window was not looking. It only speaks on transitions between two consecutive snapshots
/// (never because a state merely persists), says nothing about what the user just asked for themselves, and at most once per
/// kind per 30 seconds. There is no setting: a notification that needs switching off is one that should not have been sent.
/// </summary>
internal sealed class Notifier
{
    public static readonly TimeSpan Throttle = TimeSpan.FromSeconds(30);

    /// <summary>How long after the user's own start or stop a change of the proxy is taken as theirs.</summary>
    public static readonly TimeSpan UserWindow = TimeSpan.FromSeconds(15);

    public const string Title = "TgwsOne";
    public const string ProxyDiedText = "Прокси остановился. Откройте TgwsOne и нажмите «Подключить».";
    public const string ProxyRestartedText = "Прокси был перезапущен.";
    public const string BypassRecoveredText = "Обход DPI снова работает.";
    public const string TrayHintTitle = "TgwsOne работает в трее";
    public const string TrayHintText = "Окно закрыто, прокси продолжает работать. Чтобы выйти, откройте меню значка.";

    private readonly INotificationSink _sink;
    private readonly IClock _clock;
    private readonly object _gate = new();
    private readonly Dictionary<NoticeKind, DateTimeOffset> _lastSent = [];
    private AppSnapshot? _previous;
    private DateTimeOffset _userUntil = DateTimeOffset.MinValue;

    public Notifier(INotificationSink sink, IClock clock)
    {
        _sink = sink;
        _clock = clock;
    }

    public static string BypassErrorText(string? detail)
        => string.IsNullOrWhiteSpace(detail) ? "Обход DPI перестал работать." : $"Обход DPI перестал работать: {detail}";

    public static string PortSwitchedText(int? oldPort, int newPort)
        => $"Порт {(oldPort is { } p ? p.ToString() : "прокси")} занят другим приложением: прокси перешёл на порт {newPort}. Откройте ссылку в Telegram ещё раз.";

    public static string StartFailedText(string message) => $"Не удалось подключиться: {message}";

    /// <summary>Follows <paramref name="monitor"/>; the snapshot it has now is what later ones are compared with.</summary>
    public void Attach(StatusMonitor monitor)
    {
        lock (_gate) _previous = monitor.Current;
        monitor.Changed += OnSnapshot;
    }

    /// <summary>
    /// The user started, stopped or restarted the proxy from this app (or is about to): the change that follows is theirs, not news.
    /// Called both before and after the operation, so the window covers the poll that sees its result.
    /// </summary>
    public void UserChangedProxy()
    {
        lock (_gate) _userUntil = _clock.Now + UserWindow;
    }

    public void OnSnapshot(AppSnapshot s)
    {
        var notices = new List<(NoticeKind Kind, string Title, string Body)>();
        lock (_gate)
        {
            AppSnapshot? before = _previous;
            _previous = s;
            if (before == null) return;

            bool byUser = _clock.Now < _userUntil;

            if (before.Proxy.Listening && !s.Proxy.Listening)
            {
                if (!byUser) notices.Add((NoticeKind.ProxyDied, Title, ProxyDiedText));
            }
            else if (before.Proxy.Listening && s.Proxy.Listening && before.Proxy.Pid is { } was && s.Proxy.Pid is { } now && was != now)
            {
                if (!byUser) notices.Add((NoticeKind.ProxyRestarted, Title, ProxyRestartedText));
            }

            string? bypassBefore = before.Bypass?.State;
            string? bypassNow = s.Bypass?.State;
            if (bypassBefore != "error" && bypassNow == "error")
                notices.Add((NoticeKind.BypassError, Title, BypassErrorText(s.Bypass?.LastError)));
            else if (bypassBefore == "error" && bypassNow == "running")
                notices.Add((NoticeKind.BypassRecovered, Title, BypassRecoveredText));

            // A start that had to move to another port: Telegram still has the old one, so the link has to be opened again.
            if (s.LastStart is { Outcome: StartOutcome.PortSwitched } start && !ReferenceEquals(start, before.LastStart))
                notices.Add((NoticeKind.PortSwitched, Title, PortSwitchedText(start.OldPort, start.Port)));
        }

        foreach ((NoticeKind kind, string title, string body) in notices) Send(kind, title, body);
    }

    /// <summary>A notice that did not come from a transition (the first close to the tray, a connection that did not come up).</summary>
    public void Notify(NoticeKind kind, string title, string body) => Send(kind, title, body);

    private void Send(NoticeKind kind, string title, string body)
    {
        lock (_gate)
        {
            DateTimeOffset now = _clock.Now;
            if (_lastSent.TryGetValue(kind, out DateTimeOffset last) && now - last < Throttle) return;
            _lastSent[kind] = now;
        }
        _ = ShowAsync(title, body);
    }

    // A notification must never take the application down, whatever the desktop's side does: the sinks may throw anything.
    private async Task ShowAsync(string title, string body)
    {
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            await _sink.ShowAsync(title, body, timeout.Token).ConfigureAwait(false);
        }
        catch (Exception)
        {
            // best effort
        }
    }
}

/// <summary>
/// The proxy control the pages use, which tells the <see cref="Notifier"/> whenever the user starts or stops the proxy from this
/// app: what follows is theirs, not news. (Starts and stops made some other way, from a terminal or by a crash, pass unmarked.)
/// </summary>
internal sealed class UserActionProxyControl(IProxyControl inner, Notifier notifier) : IProxyControl
{
    public async Task<StartResult> StartAsync(CancellationToken ct = default)
    {
        notifier.UserChangedProxy();
        try { return await inner.StartAsync(ct).ConfigureAwait(false); }
        finally { notifier.UserChangedProxy(); }
    }

    public async Task<StopOutcome> StopAsync(CancellationToken ct = default)
    {
        notifier.UserChangedProxy();
        try { return await inner.StopAsync(ct).ConfigureAwait(false); }
        finally { notifier.UserChangedProxy(); }
    }

    public ProxyStatus GetStatus() => inner.GetStatus();

    public bool RestartNeeded() => inner.RestartNeeded();
}
