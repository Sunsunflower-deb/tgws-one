using System.IO.Pipes;
using System.Net.Sockets;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace TgwsOne.Core;

/// <summary>
/// What the proxy controller needs from an engine, whether it runs in this process (the window hosts it) or as a
/// detached process (the CLI started one). <see cref="ProxyController"/> and the in-process host both implement it.
/// </summary>
public interface IProxyEngineControl
{
    Task<StartResult> StartAsync(CancellationToken ct = default);
    Task<StopOutcome> StopAsync(CancellationToken ct = default);
    ProxyStatus GetStatus();
    bool RestartNeeded();
}

/// <summary>
/// A request from a CLI process to the running application, which hosts the engine. One request and one response
/// per connection, one JSON object per line — small enough to stay obvious, and only this user's processes can
/// reach it (the runtime directory and the pipe are private to the user).
/// </summary>
public sealed record EngineControlRequest(string Cmd);

/// <param name="Outcome">A <see cref="StartOutcome"/> or <see cref="StopOutcome"/> name, when the command returns one.</param>
public sealed record EngineControlResponse
{
    public bool Ok { get; init; }
    public string? Error { get; init; }
    public string? Outcome { get; init; }
    public int Port { get; init; }
    public int? OldPort { get; init; }
    public int? ExitCode { get; init; }
    public string Output { get; init; } = "";
    public bool Running { get; init; }
    public bool Listening { get; init; }
    public bool PortBusyByOther { get; init; }
    public int? Pid { get; init; }
    public bool RestartNeeded { get; init; }

    public static EngineControlResponse Failure(string error) => new() { Ok = false, Error = error };
}

public static class EngineControlCommands
{
    public const string Start = "start";
    public const string Stop = "stop";
    public const string Status = "status";
    public const string RestartNeeded = "restart-needed";
}

/// <summary>Client half: how a CLI process asks the window to start, stop or report its engine.</summary>
public static class EngineControlClient
{
    public static async Task<EngineControlResponse?> SendAsync(
        AppPaths paths, EngineControlRequest request, TimeSpan timeout, CancellationToken ct = default)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeout);
        try
        {
            if (OperatingSystem.IsWindows())
                return await SendPipeAsync(paths, request, cts.Token).ConfigureAwait(false);
            return await SendUnixAsync(paths, request, cts.Token).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is SocketException or IOException or OperationCanceledException
                                       or UnauthorizedAccessException or TimeoutException or JsonException
                                       or InvalidOperationException or ArgumentException)
        {
            return null;
        }
    }

    /// <summary>Synchronous for <see cref="ProxyController.GetStatus"/>, which is called on a polling thread.</summary>
    public static EngineControlResponse? TrySend(AppPaths paths, EngineControlRequest request, TimeSpan timeout)
        => SendAsync(paths, request, timeout).GetAwaiter().GetResult();

    private static async Task<EngineControlResponse?> SendUnixAsync(
        AppPaths paths, EngineControlRequest request, CancellationToken ct)
    {
        using var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        await socket.ConnectAsync(new UnixDomainSocketEndPoint(paths.ControlSocket), ct).ConfigureAwait(false);
        await using var stream = new NetworkStream(socket, ownsSocket: false);
        return await ExchangeAsync(stream, request, ct).ConfigureAwait(false);
    }

    private static async Task<EngineControlResponse?> SendPipeAsync(
        AppPaths paths, EngineControlRequest request, CancellationToken ct)
    {
        using var pipe = new NamedPipeClientStream(".", EngineControlServer.PipeName(paths),
            PipeDirection.InOut, PipeOptions.Asynchronous);
        await pipe.ConnectAsync(ct).ConfigureAwait(false);
        return await ExchangeAsync(pipe, request, ct).ConfigureAwait(false);
    }

    private static async Task<EngineControlResponse?> ExchangeAsync(
        Stream stream, EngineControlRequest request, CancellationToken ct)
    {
        byte[] payload = JsonSerializer.SerializeToUtf8Bytes(request);
        await stream.WriteAsync(payload, ct).ConfigureAwait(false);
        await stream.WriteAsync("\n"u8.ToArray(), ct).ConfigureAwait(false);
        await stream.FlushAsync(ct).ConfigureAwait(false);

        string? line = await ReadLineAsync(stream, 16 * 1024, ct).ConfigureAwait(false);
        return line is null ? null : JsonSerializer.Deserialize<EngineControlResponse>(line);
    }

    internal static async Task<string?> ReadLineAsync(Stream stream, int maxBytes, CancellationToken ct)
    {
        var buffer = new byte[maxBytes];
        int length = 0;
        while (length < buffer.Length)
        {
            int read = await stream.ReadAsync(buffer.AsMemory(length), ct).ConfigureAwait(false);
            if (read == 0) break;
            length += read;
            if (Array.IndexOf(buffer, (byte)'\n', 0, length) >= 0)
                return Encoding.UTF8.GetString(buffer, 0, length).Trim();
        }
        return length == 0 ? null : Encoding.UTF8.GetString(buffer, 0, length).Trim();
    }
}

/// <summary>
/// Server half, hosted by the window for as long as it runs. It answers on a Unix socket (or a named pipe on
/// Windows) even while the engine is stopped, so <c>tgwsone --start</c> can turn on the window's own engine
/// instead of launching a second one.
/// </summary>
public sealed class EngineControlServer : IAsyncDisposable
{
    private readonly AppPaths _paths;
    private readonly Func<EngineControlRequest, Task<EngineControlResponse>> _handler;
    private readonly Action<string>? _log;
    private readonly CancellationTokenSource _stop = new();
    private Socket? _listener;
    private Task _loop = Task.CompletedTask;

    private EngineControlServer(AppPaths paths,
        Func<EngineControlRequest, Task<EngineControlResponse>> handler, Action<string>? log)
    {
        _paths = paths;
        _handler = handler;
        _log = log;
    }

    public static EngineControlServer Start(AppPaths paths,
        Func<EngineControlRequest, Task<EngineControlResponse>> handler, Action<string>? log = null)
    {
        var server = new EngineControlServer(paths, handler, log);
        try
        {
            PrivateDirectory.Ensure(paths.RunDir);
            server._loop = server.RunAsync(server._stop.Token);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or SocketException
                                       or PlatformNotSupportedException or ArgumentException)
        {
            log?.Invoke($"канал управления движком не запущен ({ex.GetType().Name}: {ex.Message})");
        }
        return server;
    }

    internal static string PipeName(AppPaths paths)
    {
        byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(Path.GetFullPath(paths.RunDir)));
        return "tgwsone-engine-" + Convert.ToHexStringLower(hash)[..16];
    }

    private async Task RunAsync(CancellationToken ct)
    {
        if (OperatingSystem.IsWindows()) await RunPipeAsync(ct).ConfigureAwait(false);
        else await RunUnixAsync(ct).ConfigureAwait(false);
    }

    [UnsupportedOSPlatform("windows")]
    private async Task RunUnixAsync(CancellationToken ct)
    {
        if (File.Exists(_paths.ControlSocket)) File.Delete(_paths.ControlSocket);
        var listener = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        try
        {
            listener.Bind(new UnixDomainSocketEndPoint(_paths.ControlSocket));
            listener.Listen(8);
        }
        catch
        {
            listener.Dispose();
            throw;
        }
        _listener = listener;

        while (!ct.IsCancellationRequested)
        {
            Socket client;
            try
            {
                client = await listener.AcceptAsync(ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException or SocketException)
            {
                if (ct.IsCancellationRequested) return;
                continue;
            }
            _ = Task.Run(() => HandleUnixAsync(client, ct));
        }
    }

    [UnsupportedOSPlatform("windows")]
    private async Task HandleUnixAsync(Socket client, CancellationToken ct)
    {
        using (client)
        {
            try
            {
                await using var stream = new NetworkStream(client, ownsSocket: false);
                await ExchangeAsync(stream, ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is SocketException or IOException or OperationCanceledException
                                           or ObjectDisposedException or JsonException)
            {
                _log?.Invoke($"канал управления движком: запрос не выполнен ({ex.GetType().Name})");
            }
        }
    }

    private async Task RunPipeAsync(CancellationToken ct)
    {
        string name = PipeName(_paths);
        while (!ct.IsCancellationRequested)
        {
            NamedPipeServerStream? pipe = null;
            try
            {
                pipe = new NamedPipeServerStream(name, PipeDirection.InOut, 1, PipeTransmissionMode.Byte,
                    PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                await pipe.WaitForConnectionAsync(ct).ConfigureAwait(false);
                NamedPipeServerStream connected = pipe;
                pipe = null;
                _ = Task.Run(async () =>
                {
                    using (connected)
                    {
                        try { await ExchangeAsync(connected, ct).ConfigureAwait(false); }
                        catch (Exception ex) when (ex is IOException or OperationCanceledException or JsonException) { }
                    }
                });
            }
            catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException or IOException
                                           or UnauthorizedAccessException)
            {
                pipe?.Dispose();
                if (ct.IsCancellationRequested) return;
                try { await Task.Delay(200, ct).ConfigureAwait(false); }
                catch (OperationCanceledException) { return; }
            }
        }
    }

    private async Task ExchangeAsync(Stream stream, CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(10));
        string? line = await EngineControlClient.ReadLineAsync(stream, 16 * 1024, timeout.Token).ConfigureAwait(false);
        if (line is null) return;

        EngineControlResponse response;
        try
        {
            EngineControlRequest? request = JsonSerializer.Deserialize<EngineControlRequest>(line);
            response = request is null
                ? EngineControlResponse.Failure("пустой запрос")
                : await _handler(request).ConfigureAwait(false);
        }
        catch (JsonException)
        {
            response = EngineControlResponse.Failure("неверный запрос");
        }
        catch (Exception ex)
        {
            response = EngineControlResponse.Failure(ex.Message);
        }

        byte[] payload = JsonSerializer.SerializeToUtf8Bytes(response);
        await stream.WriteAsync(payload, timeout.Token).ConfigureAwait(false);
        await stream.WriteAsync("\n"u8.ToArray(), timeout.Token).ConfigureAwait(false);
        await stream.FlushAsync(timeout.Token).ConfigureAwait(false);
    }

    public async ValueTask DisposeAsync()
    {
        await _stop.CancelAsync().ConfigureAwait(false);
        try { _listener?.Dispose(); } catch (SocketException) { }
        try { await _loop.WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false); }
        catch (Exception ex) when (ex is TimeoutException or OperationCanceledException or AggregateException) { }
        _listener = null;
        if (!OperatingSystem.IsWindows())
        {
            try { File.Delete(_paths.ControlSocket); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { /* the next owner removes it */ }
        }
        _stop.Dispose();
    }
}
