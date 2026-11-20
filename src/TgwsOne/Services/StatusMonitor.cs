using TgwsOne.Core;
using TgwsOne.Core.Zapret;

namespace TgwsOne.Services;

/// <summary>
/// Looks at the proxy, the Telegram connections and the bypass service every couple of seconds and publishes what it sees as
/// an <see cref="AppSnapshot"/>. No network probes here (those belong to Diagnostics and the strategy finder): a status poll
/// only reads local state and asks the service over its control channel, so it is cheap and never reaches the internet.
/// </summary>
internal sealed class StatusMonitor : IAsyncDisposable
{
    private static readonly TimeSpan DefaultPeriod = TimeSpan.FromSeconds(2);

    private readonly IProxyControl _proxy;
    private readonly IControlClient _control;
    private readonly INetInfo _net;
    private readonly SynchronizationContext? _context = SynchronizationContext.Current;   // the UI thread's, when created there
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly CancellationTokenSource _stop = new();
    private readonly Task _loop;

    private AppSnapshot _current = AppSnapshot.Empty;
    private StartResult? _lastStart;
    private volatile bool _disposed;

    /// <summary>Starts polling at once (the first snapshot comes as soon as the first poll is done), then every <paramref name="period"/>.</summary>
    public StatusMonitor(IProxyControl proxy, IControlClient control, INetInfo net, TimeSpan? period = null)
    {
        _proxy = proxy;
        _control = control;
        _net = net;
        _loop = Task.Run(() => RunAsync(period ?? DefaultPeriod, _stop.Token));
    }

    /// <summary>Raised after every poll and every <see cref="RecordStart"/>, on the thread that created the monitor when it had a synchronization context (the UI thread), otherwise on the polling thread.</summary>
    public event Action<AppSnapshot>? Changed;

    public AppSnapshot Current => Volatile.Read(ref _current);

    /// <summary>Remembers the result of a start made from this window, so the headline can say why it failed; shown at once, not at the next poll.</summary>
    public void RecordStart(StartResult result)
    {
        Volatile.Write(ref _lastStart, result);
        Publish(Current with { LastStart = result });
    }

    /// <summary>One poll now, for after the user did something (a start, a stop, switching the bypass) and wants to see the result.</summary>
    public async Task RefreshNowAsync()
    {
        if (_disposed) return;
        try
        {
            await RefreshAsync(_stop.Token);
        }
        catch (Exception ex) when (_disposed && ex is OperationCanceledException or ObjectDisposedException)
        {
            // disposed while waiting (a window closing as the user's click lands): nothing to refresh any more
        }
    }

    public async ValueTask DisposeAsync()
    {
        _disposed = true;
        await _stop.CancelAsync();
        try { await _loop; }
        catch (OperationCanceledException) { }
        _stop.Dispose();
        _gate.Dispose();
    }

    private async Task RunAsync(TimeSpan period, CancellationToken ct)
    {
        using var timer = new PeriodicTimer(period);
        try
        {
            do
            {
                try
                {
                    await RefreshAsync(ct);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    // A poll that fails must not end the monitoring: the next tick tries again.
                }
            }
            while (await timer.WaitForNextTickAsync(ct));
        }
        catch (OperationCanceledException)
        {
            // stopping
        }
    }

    private async Task RefreshAsync(CancellationToken ct)
    {
        await _gate.WaitAsync(ct);   // a poll and a RefreshNowAsync never overlap
        try
        {
            ProxyStatus proxy = await Task.Run(_proxy.GetStatus, ct);   // reads files and probes the port: not on the UI thread
            int connections = proxy.Listening ? await Task.Run(() => TelegramConnections.Count(proxy.Port, _net), ct) : 0;

            ZapretStatus? bypass = null;
            bool reachable;
            try
            {
                // Even a refused request carries the status, so this is what the service says either way.
                ControlResponse r = await _control.SendAsync(new ControlRequest(ControlCommands.Status), ct);
                bypass = r.Status;
                reachable = true;
            }
            catch (ServiceUnavailableException)
            {
                reachable = false;
            }

            Publish(new AppSnapshot(proxy, Volatile.Read(ref _lastStart), connections, bypass, reachable));
        }
        finally
        {
            _gate.Release();
        }
    }

    private void Publish(AppSnapshot snapshot)
    {
        Volatile.Write(ref _current, snapshot);
        Action<AppSnapshot>? handler = Changed;
        if (handler is null) return;

        void Raise()
        {
            // One by one: an exception in one subscriber must neither stop the monitoring nor keep the ones after it from hearing.
            foreach (Delegate subscriber in handler.GetInvocationList())
            {
                try { ((Action<AppSnapshot>)subscriber)(snapshot); }
                catch { /* that subscriber's problem */ }
            }
        }

        if (_context is null) Raise();
        else _context.Post(_ => Raise(), null);
    }
}
