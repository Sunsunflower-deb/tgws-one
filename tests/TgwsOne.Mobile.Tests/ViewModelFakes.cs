using System.Net;
using TgwsOne.Core;
using TgwsOne.Engine.Desync;

namespace TgwsOne.Mobile.Tests;

internal sealed class FakeBypassControl : IDesyncControl
{
    public IReadOnlyList<DesyncStrategy> Rotation { get; set; } = BuiltInDesync.BuiltIn;
    public IReadOnlyList<AutoHost> AutoHosts { get; set; } = [];
    public string Strategy { get; set; } = DesyncFinder.AutoId;
    public bool Enabled { get; set; } = true;
    public List<string> Applied { get; } = [];
    public List<FinderBaseline> Restored { get; } = [];
    public Func<string, string?>? Refuse { get; set; }

    public Task<FinderBaseline> CaptureAsync(CancellationToken ct = default)
        => Task.FromResult(new FinderBaseline(Strategy, Enabled));

    public Task<string?> ApplyAsync(string candidateId, CancellationToken ct = default)
    {
        Applied.Add(candidateId);
        if (Refuse?.Invoke(candidateId) is { } problem) return Task.FromResult<string?>(problem);

        Strategy = candidateId;
        Enabled = candidateId != DesyncFinder.NoBypassId;
        return Task.FromResult<string?>(null);
    }

    public Task<string?> RestoreAsync(FinderBaseline baseline, CancellationToken ct = default)
    {
        Restored.Add(baseline);
        Strategy = baseline.Strategy;
        Enabled = baseline.Enabled;
        return Task.FromResult<string?>(null);
    }

    public DesyncStrategy? Resolve(string sni, string route) => null;
}

internal sealed class FakeMobileSettings : IMobileSettings
{
    public const string InitialSecret = "0123456789abcdef0123456789abcdef";

    public AndroidSettings Android { get; set; } = AndroidSettings.Default;
    public ProxySettings Proxy { get; set; } = new(IPAddress.Loopback, 1443, [], CfProxy: true, H2: true,
        NoSecure: false, Verbose: true, [], [], 256, 4, 5, InitialSecret);
    public string Link { get; set; } = $"tg://proxy?server=127.0.0.1&port=1443&secret=dd{InitialSecret}";
    public int Saves { get; private set; }

    public AndroidSettings LoadAndroid() => Android;
    public ProxySettings LoadProxy() => Proxy;
    public string TelegramLink() => Link;

    public void SaveAndroid(AndroidSettings settings)
    {
        Saves++;
        Android = settings;
    }
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

internal sealed class FakeTicker : ITicker
{
    private readonly List<Action> _ticks = [];

    public int Started { get; private set; }
    public int Stopped { get; private set; }
    public TimeSpan? Period { get; private set; }

    public IDisposable Start(TimeSpan period, Action tick)
    {
        Started++;
        Period = period;
        _ticks.Add(tick);
        return new Stop(this, tick);
    }

    public void Tick()
    {
        foreach (Action tick in _ticks.ToArray()) tick();
    }

    private sealed class Stop(FakeTicker owner, Action tick) : IDisposable
    {
        public void Dispose()
        {
            owner.Stopped++;
            owner._ticks.Remove(tick);
        }
    }
}

internal sealed class FakeDiagnosticsActions : IDiagnosticsActions
{
    public int ChangePorts { get; private set; }
    public int Connects { get; private set; }
    public int Opened { get; private set; }
    public int Enabled { get; private set; }
    public int Found { get; private set; }
    public string? Problem { get; set; }

    public Task<string?> ChangePortAsync()
    {
        ChangePorts++;
        return Task.FromResult(Problem);
    }

    public Task<string?> ConnectAsync()
    {
        Connects++;
        return Task.FromResult(Problem);
    }

    public Task<string?> OpenInTelegramAsync()
    {
        Opened++;
        return Task.FromResult(Problem);
    }

    public Task<string?> EnableBypassAsync()
    {
        Enabled++;
        return Task.FromResult(Problem);
    }

    public Task<string?> FindStrategyAsync()
    {
        Found++;
        return Task.FromResult(Problem);
    }
}
