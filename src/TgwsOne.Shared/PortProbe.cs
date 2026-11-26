using System.Net;
using System.Net.Sockets;

namespace TgwsOne.Core;

public static class PortProbe
{
    /// <summary>
    /// True if the port is taken. Two checks, because either alone misses cases: a bind probe on the configured
    /// host, and a TCP connect to the address a client would use (on Windows a specific-address bind can succeed
    /// while another socket listens on 0.0.0.0).
    /// </summary>
    public static bool IsBusy(IPAddress host, int port)
    {
        try
        {
            using var probe = new TcpListener(host, port);
            probe.Start();
            probe.Stop();
        }
        catch (SocketException ex) when (ex.SocketErrorCode is SocketError.AddressAlreadyInUse or SocketError.AccessDenied)
        {
            // AccessDenied: on Windows a port held with exclusive access (or reserved) fails this way.
            return true;
        }
        catch (SocketException)
        {
            // Not a busy port, e.g. a host address this machine does not own. The engine will report that itself.
        }
        return CanConnect(TelegramLink.ConnectAddress(host), port, 300);
    }

    /// <summary>First free port in <c>first … first+count-1</c> (never above 65535), or null if all are busy.</summary>
    public static int? FindFree(IPAddress host, int first, int count)
    {
        int last = Math.Min(first + count - 1, 65535);
        for (int port = first; port <= last; port++)
            if (!IsBusy(host, port)) return port;
        return null;
    }

    /// <summary>True if a TCP connection to the address succeeds within the timeout. Blocks for at most that long.</summary>
    public static bool CanConnect(IPAddress address, int port, int timeoutMs)
        => CanConnectAsync(address, port, timeoutMs).GetAwaiter().GetResult();

    /// <summary>
    /// Async form of <see cref="CanConnect"/>. Only <paramref name="ct"/> can make it throw; a refused,
    /// unreachable or timed-out connection is simply <c>false</c>.
    /// </summary>
    public static async Task<bool> CanConnectAsync(IPAddress address, int port, int timeoutMs, CancellationToken ct = default)
    {
        try
        {
            using var client = new TcpClient(address.AddressFamily);
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(timeoutMs);
            // ConfigureAwait(false): the sync wrapper above must not deadlock on a UI synchronization context.
            await client.ConnectAsync(address, port, cts.Token).ConfigureAwait(false);
            return client.Connected;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            return false;
        }
    }
}
