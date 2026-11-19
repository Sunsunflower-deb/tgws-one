using System.Collections.Concurrent;
using System.IO.Pipes;
using System.Net.Sockets;
using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;

namespace TgwsOne.Core.Zapret;

/// <summary>
/// Listens on the control endpoint (Unix socket on Linux, named pipe on Windows) and answers one request per
/// connection. Connections are read concurrently, but the handler runs one request at a time. Nothing a
/// client sends can crash it: bad input becomes an error response, a faulting handler a generic one.
/// </summary>
public sealed class ControlServer
{
    private const int MaxConnections = 64;
    private const long MaxDrainBytes = 8 * 1024 * 1024;
    private static readonly TimeSpan WriteTimeout = TimeSpan.FromSeconds(5);

    private readonly ServicePaths _paths;
    private readonly IControlHandler _handler;
    private readonly AppLog _log;
    private readonly TimeSpan _readTimeout;
    private readonly SemaphoreSlim _handlerGate = new(1, 1);
    private readonly SemaphoreSlim _slots = new(MaxConnections, MaxConnections);
    private readonly ConcurrentDictionary<Task, byte> _connections = new();
    private readonly TaskCompletionSource _listening = new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <param name="readTimeout">How long a client may take to send its request line (5 s by default).</param>
    public ControlServer(ServicePaths paths, IControlHandler handler, AppLog log, TimeSpan? readTimeout = null)
    {
        _paths = paths;
        _handler = handler;
        _log = log;
        _readTimeout = readTimeout ?? TimeSpan.FromSeconds(5);
    }

    /// <summary>Completes once the endpoint accepts connections; faults if it could not be set up.</summary>
    public Task WhenListening => _listening.Task;

    /// <summary>Serves until cancelled, then closes the endpoint (Linux: removes the socket file) and returns.</summary>
    public async Task RunAsync(CancellationToken ct)
    {
        try
        {
            if (OperatingSystem.IsWindows()) await RunPipeAsync(ct);
            else await RunSocketAsync(ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            _listening.TrySetCanceled(ct);
        }
        catch (Exception ex)
        {
            _listening.TrySetException(ex);
            throw;
        }
    }

    [UnsupportedOSPlatform("windows")]
    private async Task RunSocketAsync(CancellationToken ct)
    {
        string path = _paths.Endpoint;
        string? dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        if (File.Exists(path)) File.Delete(path);   // left behind by a crashed run

        using var listener = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        listener.Bind(new UnixDomainSocketEndPoint(path));
        // Every local user may talk to the service: the commands are the closed set the protocol allows.
        File.SetUnixFileMode(path,
            UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead | UnixFileMode.GroupWrite |
            UnixFileMode.OtherRead | UnixFileMode.OtherWrite);
        listener.Listen(32);
        _listening.TrySetResult();

        try
        {
            while (true)
            {
                Socket client = await listener.AcceptAsync(ct);
                Accept(new NetworkStream(client, ownsSocket: true), ct);
            }
        }
        finally
        {
            listener.Close();
            try { File.Delete(path); } catch { /* best effort */ }
            await DrainConnectionsAsync();
        }
    }

    [SupportedOSPlatform("windows")]
    private async Task RunPipeAsync(CancellationToken ct)
    {
        PipeSecurity security = CreatePipeSecurity();
        bool first = true;
        try
        {
            while (true)
            {
                // The next instance exists before the current client is served, so a second client never finds no pipe.
                // The very first one must create the name: someone else already holding it must not be taken over.
                NamedPipeServerStream pipe = NamedPipeServerStreamAcl.Create(
                    _paths.Endpoint, PipeDirection.InOut, NamedPipeServerStream.MaxAllowedServerInstances,
                    PipeTransmissionMode.Byte, PipeOptions.Asynchronous | (first ? PipeOptions.FirstPipeInstance : PipeOptions.None),
                    0, 0, security);
                if (first)
                {
                    first = false;
                    _listening.TrySetResult();
                }
                try
                {
                    await pipe.WaitForConnectionAsync(ct);
                }
                catch
                {
                    await pipe.DisposeAsync();
                    throw;
                }
                Accept(pipe, ct);
            }
        }
        finally
        {
            await DrainConnectionsAsync();
        }
    }

    // SYSTEM and Administrators: everything. Interactive (S-1-5-4, i.e. someone sitting at this machine): read/write,
    // which is all the commands need. Network logons are not in that group, so the pipe is not reachable remotely.
    [SupportedOSPlatform("windows")]
    private static PipeSecurity CreatePipeSecurity()
    {
        var security = new PipeSecurity();
        security.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null), PipeAccessRights.FullControl, AccessControlType.Allow));
        security.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null), PipeAccessRights.FullControl, AccessControlType.Allow));
        security.AddAccessRule(new PipeAccessRule(new SecurityIdentifier("S-1-5-4"), PipeAccessRights.ReadWrite, AccessControlType.Allow));
        return security;
    }

    private void Accept(Stream stream, CancellationToken ct)
    {
        if (!_slots.Wait(0))
        {
            stream.Dispose();   // too many open connections at once: drop the newcomer
            return;
        }
        // Registered before the work starts, so a connection that ends at once can't be added after it has gone.
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _connections[done.Task] = 0;
        _ = Task.Run(async () =>
        {
            try { await ServeAsync(stream, ct); }
            finally
            {
                _slots.Release();
                _connections.TryRemove(done.Task, out _);
                done.SetResult();
            }
        }, CancellationToken.None);
    }

    private async Task DrainConnectionsAsync()
    {
        // Cancellation has reached every connection already; give them a moment to unwind.
        try { await Task.WhenAll(_connections.Keys).WaitAsync(TimeSpan.FromSeconds(5)); }
        catch { /* shutting down anyway */ }
    }

    private async Task ServeAsync(Stream stream, CancellationToken ct)
    {
        await using (stream)
        {
            try
            {
                LineRead read;
                using (var readCts = CancellationTokenSource.CreateLinkedTokenSource(ct))
                {
                    readCts.CancelAfter(_readTimeout);
                    try
                    {
                        read = await ControlIo.ReadLineAsync(stream, ControlProtocol.MaxLineBytes, readCts.Token);
                    }
                    catch (OperationCanceledException)
                    {
                        return;   // too slow, or the service is stopping: drop the connection
                    }

                    if (read.TooLong)
                    {
                        // Answer first, then swallow the rest of the line so the close doesn't reset the connection.
                        await WriteAsync(stream, new ControlResponse(false, ControlProtocol.RequestTooLong), ct);
                        if (!read.SawNewline)
                        {
                            try { await ControlIo.DrainLineAsync(stream, MaxDrainBytes, readCts.Token); }
                            catch (OperationCanceledException) { /* enough */ }
                        }
                        return;
                    }
                }
                if (read.Line == null) return;   // connected and left without a request

                ControlRequest? request = ControlProtocol.ParseRequest(read.Line, out string? error);
                ControlResponse response = request == null
                    ? new ControlResponse(false, error)
                    : await InvokeHandlerAsync(request, ct);
                await WriteAsync(stream, response, ct);
            }
            catch (OperationCanceledException) { /* stopping */ }
            catch (Exception ex) when (ex is IOException or SocketException or ObjectDisposedException)
            {
                // The client went away before it got its answer.
            }
            catch (Exception ex)
            {
                _log.Write($"control: connection failed: {ex}");
            }
        }
    }

    private async Task<ControlResponse> InvokeHandlerAsync(ControlRequest request, CancellationToken ct)
    {
        await _handlerGate.WaitAsync(ct);
        try
        {
            return await _handler.HandleAsync(request, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _log.Write($"control: command '{request.Cmd}' failed: {ex}");
            return new ControlResponse(false, "внутренняя ошибка службы");
        }
        finally
        {
            _handlerGate.Release();
        }
    }

    private static async Task WriteAsync(Stream stream, ControlResponse response, CancellationToken ct)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(WriteTimeout);
        await ControlIo.WriteLineAsync(stream, ControlProtocol.Serialize(response), cts.Token);
    }
}
