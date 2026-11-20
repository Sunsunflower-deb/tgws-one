using System.Net;
using System.Net.NetworkInformation;
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

/// <summary>One row of the machine's TCP table.</summary>
internal readonly record struct TcpConnection(IPEndPoint Local, IPEndPoint Remote, TcpState State);

/// <summary>What the UI needs to know about the machine's network, so it can be faked.</summary>
internal interface INetInfo
{
    IReadOnlyList<TcpConnection> TcpConnections();
}

internal sealed class SystemNetInfo : INetInfo
{
    public IReadOnlyList<TcpConnection> TcpConnections()
        => [.. IPGlobalProperties.GetIPGlobalProperties().GetActiveTcpConnections()
                .Select(c => new TcpConnection(c.LocalEndPoint, c.RemoteEndPoint, c.State))];
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

/// <summary>The user's settings file as the pages need it. Every call reads the file afresh: a start that had to switch ports rewrites it behind our back.</summary>
internal interface IAppSettings
{
    /// <summary>The proxy settings as they are on disk now (defaults where a value is missing or wrong); never throws.</summary>
    ProxySettings Load();

    /// <summary>The <c>tg://proxy</c> link for Telegram on this machine; makes the secret first if there is none yet.</summary>
    string TelegramLink();

    /// <summary>Replaces the secret with a new random one and saves it. The running proxy keeps the old one until it is restarted.</summary>
    void ReplaceSecret();
}

/// <summary>Opens a URL in whatever the system uses for it (Telegram for <c>tg://</c>).</summary>
internal interface ILauncher
{
    /// <returns>False when nothing could be started for the URL.</returns>
    Task<bool> OpenAsync(string url);
}

internal interface IClipboardService
{
    Task SetTextAsync(string text);
}

internal interface IDialogService
{
    /// <returns>True when the user chose <paramref name="accept"/>.</returns>
    Task<bool> ConfirmAsync(string title, string message, string accept, string cancel);
}
