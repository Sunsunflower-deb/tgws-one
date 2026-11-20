using System.Net;
using System.Net.NetworkInformation;
using TgwsOne.Core;
using TgwsOne.Core.Zapret;
using TgwsOne.Services;

namespace TgwsOne.Tests;

/// <summary>Values the tests build statuses from, so a test reads like the spec table it checks.</summary>
internal static class Samples
{
    public static ProxyStatus ProxyStopped => new(Running: false, Listening: false, Port: 1443, PortBusyByOther: false, Pid: null);

    public static ProxyStatus ProxyListening(int port = 1443) => new(Running: true, Listening: true, Port: port, PortBusyByOther: false, Pid: 4242);

    public static ZapretStatus Bypass(string state = "running", string strategy = "auto", string? lastError = null, params AutoHost[] auto)
        => new(Enabled: state != "disabled", strategy, state, Pid: state == "running" ? 777 : null, Since: null, Restarts: 0, lastError,
               Bundle: "v1.0.7", Auto: strategy == "auto" ? auto : null);

    public static AutoHost Host(string host, string strategy, int failures = 0) => new(host, strategy, failures);

    public static AppSnapshot Snapshot(ProxyStatus? proxy = null, StartResult? lastStart = null, int connections = 0, ZapretStatus? bypass = null, bool reachable = true)
        => new(proxy ?? ProxyStopped, lastStart, connections, bypass, reachable);

    public static TcpConnection Tcp(int localPort, int remotePort, TcpState state = TcpState.Established)
        => new(new IPEndPoint(IPAddress.Loopback, localPort), new IPEndPoint(IPAddress.Loopback, remotePort), state);
}

/// <summary>A proxy that is whatever the test says; starting it successfully makes it listen.</summary>
internal sealed class FakeProxyControl : IProxyControl
{
    private int _starts;
    private int _stops;

    public ProxyStatus Status { get; set; } = Samples.ProxyStopped;
    public StartResult NextStart { get; set; } = new(StartOutcome.Started, 1443);
    public bool RestartIsNeeded { get; set; }
    public int Starts => Volatile.Read(ref _starts);
    public int Stops => Volatile.Read(ref _stops);

    /// <summary>When set, a start waits for it (to look at the window while a start is in progress).</summary>
    public Task? StartGate { get; set; }

    /// <summary>When set, a start throws it (another start held the lock too long, say).</summary>
    public Exception? StartThrows { get; set; }

    public async Task<StartResult> StartAsync(CancellationToken ct = default)
    {
        Interlocked.Increment(ref _starts);
        if (StartGate is { } gate) await gate;
        if (StartThrows is { } ex) throw ex;
        if (NextStart.Ok) Status = Samples.ProxyListening(NextStart.Port);
        return NextStart;
    }

    public Task<StopOutcome> StopAsync(CancellationToken ct = default)
    {
        Interlocked.Increment(ref _stops);
        bool was = Status.Running;
        Status = Samples.ProxyStopped;
        return Task.FromResult(was ? StopOutcome.Stopped : StopOutcome.NotRunning);
    }

    /// <summary>Replaces <see cref="Status"/> when set (to make the status call throw, say).</summary>
    public Func<ProxyStatus>? OnGetStatus { get; set; }

    public ProxyStatus GetStatus() => OnGetStatus?.Invoke() ?? Status;
    public bool RestartNeeded() => RestartIsNeeded;
}

/// <summary>A bypass service that answers from <see cref="Handler"/> (by default: <c>ok</c> with <see cref="Status"/>) or is not there at all.</summary>
internal sealed class FakeControlClient : IControlClient
{
    private readonly List<ControlRequest> _sent = [];

    public bool Unavailable { get; set; }
    public ZapretStatus Status { get; set; } = Samples.Bypass();
    public Func<ControlRequest, ControlResponse>? Handler { get; set; }

    /// <summary>What was asked so far (a copy: the monitor asks from a background thread).</summary>
    public IReadOnlyList<ControlRequest> Sent { get { lock (_sent) return [.. _sent]; } }

    public Task<ControlResponse> SendAsync(ControlRequest request, CancellationToken ct = default)
    {
        lock (_sent) _sent.Add(request);
        if (Unavailable) throw new ServiceUnavailableException("служба обхода DPI недоступна");
        return Task.FromResult(Handler?.Invoke(request) ?? new ControlResponse(true, Status: Status));
    }
}

/// <summary>A TCP table the test fills in.</summary>
internal sealed class FakeNetInfo : INetInfo
{
    private readonly List<TcpConnection> _connections = [];

    public void Set(params TcpConnection[] connections)
    {
        lock (_connections)
        {
            _connections.Clear();
            _connections.AddRange(connections);
        }
    }

    public IReadOnlyList<TcpConnection> TcpConnections()
    {
        lock (_connections) return [.. _connections];
    }
}

internal sealed class FixedClock(DateTimeOffset start) : IClock
{
    public DateTimeOffset Now { get; set; } = start;

    public void Advance(TimeSpan by) => Now += by;
}

internal sealed class FakeAppSettings : IAppSettings
{
    public const string Secret = "0123456789abcdef0123456789abcdef";

    public ProxySettings Settings { get; set; } = new(IPAddress.Loopback, 1443, [], CfProxy: true, H2: true, NoSecure: false, Verbose: true, [], [], 256, 4, 5, Secret);
    public string Link { get; set; } = $"tg://proxy?server=127.0.0.1&port=1443&secret=dd{Secret}";
    public int SecretsReplaced { get; private set; }

    public ProxySettings Load() => Settings;
    public string TelegramLink() => Link;
    public void ReplaceSecret() => SecretsReplaced++;
}

internal sealed class FakeLauncher : ILauncher
{
    public List<string> Opened { get; } = [];
    public bool Succeeds { get; set; } = true;

    public Task<bool> OpenAsync(string url)
    {
        Opened.Add(url);
        return Task.FromResult(Succeeds);
    }
}

internal sealed class FakeClipboard : IClipboardService
{
    public string? Text { get; private set; }

    public Task SetTextAsync(string text)
    {
        Text = text;
        return Task.CompletedTask;
    }
}

/// <summary>Answers every question with the next of <see cref="Answers"/> (yes when none is left) and remembers what was asked.</summary>
internal sealed class FakeDialogs : IDialogService
{
    public Queue<bool> Answers { get; } = new();
    public List<string> Asked { get; } = [];

    public Task<bool> ConfirmAsync(string title, string message, string accept, string cancel)
    {
        Asked.Add(title);
        return Task.FromResult(Answers.Count == 0 || Answers.Dequeue());
    }
}

internal sealed class FakeNavigator : TgwsOne.ViewModels.INavigator
{
    public List<(TgwsOne.ViewModels.AppPage Page, bool StartCheck)> Shown { get; } = [];

    public void Show(TgwsOne.ViewModels.AppPage page, bool startCheck = false) => Shown.Add((page, startCheck));
}
