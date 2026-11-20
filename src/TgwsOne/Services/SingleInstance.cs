using System.Net.Sockets;
using System.Runtime.Versioning;
using System.Text;

namespace TgwsOne.Services;

internal enum InstanceRole
{
    /// <summary>Nobody else is running: this process is the application and listens for the others.</summary>
    First,

    /// <summary>Another instance is running (and was told what the caller asked for): this process has nothing to do.</summary>
    Second,

    /// <summary>The arbiter could not be set up (an unwritable directory, a path too long for a socket): carry on as the only instance, unguarded.</summary>
    Unavailable,
}

/// <summary>
/// One window per user. The instance that holds <c>app.lock</c> is the application and listens on <c>app.sock</c>; a later launch
/// finds the lock taken, connects to the socket, says <c>show</c> and has nothing more to do. The lock is the arbiter (the kernel
/// drops it when its holder dies, so a crash leaves nothing stale and two simultaneous launches cannot both win); the socket only
/// carries the word.
/// </summary>
internal sealed class SingleInstance : IDisposable
{
    public const string ShowMessage = "show";

    private static readonly TimeSpan HandshakeTime = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan ReadTime = TimeSpan.FromSeconds(1);
    private const int MaxMessageBytes = 64;

    private readonly FileStream? _lock;
    private readonly Socket? _listener;
    private readonly string _socketPath;
    private readonly CancellationTokenSource _stop = new();
    private readonly Task _accepting = Task.CompletedTask;

    private SingleInstance(InstanceRole role, FileStream? lockFile, Socket? listener, string socketPath)
    {
        Role = role;
        _lock = lockFile;
        _listener = listener;
        _socketPath = socketPath;
        if (listener != null) _accepting = Task.Run(AcceptLoopAsync);
    }

    public InstanceRole Role { get; }

    /// <summary>A later launch asked for the window. Raised on a thread-pool thread.</summary>
    public event Action? ShowRequested;

    /// <param name="socketPath">The socket; the lock sits beside it, named <c>app.lock</c>.</param>
    /// <param name="signal">What to tell a running instance (<see cref="ShowMessage"/>), or null to say nothing (a quiet launch, like the autostart's).</param>
    /// <param name="log">Where a setup failure is written.</param>
    public static SingleInstance Acquire(string socketPath, string? signal, Action<string>? log = null)
    {
        string lockPath = Path.Combine(Path.GetDirectoryName(socketPath) ?? ".", "app.lock");
        FileStream? lockFile = null;
        try
        {
            EnsureDirectory(Path.GetDirectoryName(lockPath)!);
            try
            {
                lockFile = new FileStream(lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            }
            catch (IOException)
            {
                // The lock is held: somebody is the application. The socket may not be listening yet if they have only just started.
                if (Signal(socketPath, signal)) return new SingleInstance(InstanceRole.Second, null, null, socketPath);
                log?.Invoke("второй запуск: первый экземпляр держит блокировку, но не отвечает на сокете");
                return new SingleInstance(InstanceRole.Second, null, null, socketPath);
            }

            // Ours. Whatever file is at the socket's path was left by an instance that died without cleaning up.
            if (File.Exists(socketPath)) File.Delete(socketPath);
            var listener = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
            try
            {
                listener.Bind(new UnixDomainSocketEndPoint(socketPath));
                listener.Listen(8);
            }
            catch
            {
                listener.Dispose();
                throw;
            }
            return new SingleInstance(InstanceRole.First, lockFile, listener, socketPath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or SocketException or ArgumentException or PlatformNotSupportedException)
        {
            lockFile?.Dispose();
            log?.Invoke($"защита от второго экземпляра не настроена ({ex.GetType().Name}: {ex.Message})");
            return new SingleInstance(InstanceRole.Unavailable, null, null, socketPath);
        }
    }

    private static void EnsureDirectory(string directory)
    {
        if (Directory.Exists(directory)) return;
        if (OperatingSystem.IsWindows()) Directory.CreateDirectory(directory);
        else CreateOwnerOnlyDirectory(directory);
    }

    [UnsupportedOSPlatform("windows")]
    private static void CreateOwnerOnlyDirectory(string directory)
        => Directory.CreateDirectory(directory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);

    /// <summary>Connects to the running instance and tells it <paramref name="signal"/>; false when nobody answers in time.</summary>
    private static bool Signal(string socketPath, string? signal)
    {
        DateTime limit = DateTime.UtcNow + HandshakeTime;
        while (true)
        {
            try
            {
                using var client = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
                client.Connect(new UnixDomainSocketEndPoint(socketPath));
                if (signal != null) client.Send(Encoding.UTF8.GetBytes(signal + "\n"));
                return true;
            }
            catch (Exception ex) when (ex is SocketException or IOException or ArgumentException)
            {
                if (DateTime.UtcNow > limit) return false;
                Thread.Sleep(50);
            }
        }
    }

    private async Task AcceptLoopAsync()
    {
        while (!_stop.IsCancellationRequested)
        {
            Socket client;
            try
            {
                client = await _listener!.AcceptAsync(_stop.Token);
            }
            catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException or SocketException)
            {
                if (_stop.IsCancellationRequested) return;
                continue;   // a client that went away before it was accepted
            }
            _ = Task.Run(() => HandleAsync(client));   // one slow or silent client must not hold up the next
        }
    }

    private async Task HandleAsync(Socket client)
    {
        using (client)
        {
            try
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(_stop.Token);
                timeout.CancelAfter(ReadTime);
                var buffer = new byte[MaxMessageBytes];
                int length = 0;
                while (length < buffer.Length)
                {
                    int read = await client.ReceiveAsync(buffer.AsMemory(length), SocketFlags.None, timeout.Token);
                    if (read == 0) break;
                    length += read;
                    if (Array.IndexOf(buffer, (byte)'\n', 0, length) >= 0) break;
                }
                string message = Encoding.UTF8.GetString(buffer, 0, length).Trim();
                if (message == ShowMessage) ShowRequested?.Invoke();
            }
            catch (Exception ex) when (ex is OperationCanceledException or SocketException or ObjectDisposedException or IOException)
            {
                // a client that said nothing, or hung up: nothing to do
            }
        }
    }

    public void Dispose()
    {
        _stop.Cancel();
        _listener?.Dispose();
        try { _accepting.Wait(TimeSpan.FromSeconds(1)); }
        catch (AggregateException) { /* the loop ends by cancellation */ }
        if (Role == InstanceRole.First)
        {
            try { File.Delete(_socketPath); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { /* the next owner removes it */ }
        }
        _lock?.Dispose();
        _stop.Dispose();
    }
}
