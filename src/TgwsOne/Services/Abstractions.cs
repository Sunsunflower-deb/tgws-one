using TgwsOne.Core;

namespace TgwsOne.Services;

/// <summary>What the UI needs from the proxy: start, stop, look. An adapter over <see cref="ProxyController"/> so view-models can be tested with a fake.</summary>
internal interface IProxyControl
{
    Task<StartResult> StartAsync(CancellationToken ct = default);
    Task<StopOutcome> StopAsync(CancellationToken ct = default);
    ProxyStatus GetStatus();
    bool RestartNeeded();
}

internal sealed class ProxyControl(ProxyController controller) : IProxyControl
{
    public Task<StartResult> StartAsync(CancellationToken ct = default) => controller.StartAsync(ct);
    public Task<StopOutcome> StopAsync(CancellationToken ct = default) => controller.StopAsync(ct);
    public ProxyStatus GetStatus() => controller.GetStatus();
    public bool RestartNeeded() => controller.RestartNeeded();
}

/// <summary>Time, so throttles and timeouts can be tested without waiting.</summary>
internal interface IClock
{
    DateTimeOffset Now { get; }
}

internal sealed class SystemClock : IClock
{
    public DateTimeOffset Now => DateTimeOffset.Now;
}
