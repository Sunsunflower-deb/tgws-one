using System.IO.Pipes;
using System.Net.Sockets;
using System.Runtime.Versioning;

namespace TgwsOne.Core.Zapret;

/// <summary>The control endpoint can't be reached, or didn't answer in time. The message is for the user.</summary>
public sealed class ServiceUnavailableException(string message) : Exception(message);

/// <summary>What the UI needs from <see cref="ControlClient"/>: a seam to put a fake behind in tests.</summary>
public interface IControlClient
{
    /// <exception cref="ServiceUnavailableException">Nobody is listening, or nothing came back in time.</exception>
    Task<ControlResponse> SendAsync(ControlRequest request, CancellationToken ct = default);
}

/// <summary>Sends one request to the service and returns its answer.</summary>
public sealed class ControlClient : IControlClient
{
    private const int MaxResponseBytes = 1024 * 1024;   // the exclusion lists can outgrow a request line
    private const int PipeConnectMs = 2000;

    private readonly ServicePaths _paths;
    private readonly TimeSpan _timeout;
    private readonly Func<CancellationToken, Task<Stream>> _connect;

    /// <param name="timeout">For the whole exchange (default 5 s).</param>
    public ControlClient(ServicePaths paths, TimeSpan? timeout = null) : this(paths, timeout, null)
    {
    }

    /// <param name="connect">How the connection is made; the operating system's own way unless a test says otherwise.</param>
    internal ControlClient(ServicePaths paths, TimeSpan? timeout, Func<CancellationToken, Task<Stream>>? connect)
    {
        _paths = paths;
        _timeout = timeout ?? TimeSpan.FromSeconds(5);
        _connect = connect ?? ConnectAsync;
    }

    /// <exception cref="ServiceUnavailableException">Nobody is listening, or nothing came back in time.</exception>
    public async Task<ControlResponse> SendAsync(ControlRequest request, CancellationToken ct = default)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(_timeout);
        try
        {
            Stream stream = await _connect(cts.Token);
            await using (stream)
            {
                await ControlIo.WriteLineAsync(stream, ControlProtocol.Serialize(request), cts.Token);
                LineRead read = await ControlIo.ReadLineAsync(stream, MaxResponseBytes, cts.Token);
                if (read.Line == null) throw new ServiceUnavailableException("служба обхода DPI не ответила");
                return ControlProtocol.ParseResponse(read.Line);
            }
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new ServiceUnavailableException("служба обхода DPI не отвечает");
        }
        catch (TimeoutException)
        {
            throw new ServiceUnavailableException("служба обхода DPI недоступна");
        }
        catch (Exception ex) when (ex is SocketException or IOException)
        {
            throw new ServiceUnavailableException("служба обхода DPI недоступна");
        }
        catch (UnauthorizedAccessException)
        {
            // Windows: the pipe is open to interactive logons only (see ControlServer), so a network logon, such as an SSH session,
            // is refused. That is the boundary working, not a crash: say so in words.
            throw new ServiceUnavailableException("нет доступа к службе обхода DPI: ею управляют только из своего сеанса на этом компьютере, не по сети");
        }
    }

    private async Task<Stream> ConnectAsync(CancellationToken ct)
    {
        if (OperatingSystem.IsWindows()) return await ConnectPipeAsync(ct);

        var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        try
        {
            await socket.ConnectAsync(new UnixDomainSocketEndPoint(_paths.Endpoint), ct);
            return new NetworkStream(socket, ownsSocket: true);
        }
        catch
        {
            socket.Dispose();
            throw;
        }
    }

    [SupportedOSPlatform("windows")]
    private async Task<Stream> ConnectPipeAsync(CancellationToken ct)
    {
        var pipe = new NamedPipeClientStream(".", _paths.Endpoint, PipeDirection.InOut, PipeOptions.Asynchronous);
        try
        {
            // Waits for a free instance; with no pipe at all it gives up after the connect timeout (TimeoutException).
            await pipe.ConnectAsync(PipeConnectMs, ct);
            return pipe;
        }
        catch
        {
            await pipe.DisposeAsync();
            throw;
        }
    }
}
