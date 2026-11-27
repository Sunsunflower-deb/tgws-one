using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace TgwsOne.Android;

/// <summary>
/// P/Invoke binding of the Rust relay core (<c>libtgws_core.so</c>). The contract — function table, JSON config and
/// status, callback lifetime and ownership — is <c>docs/rust-ffi.md</c>.
/// </summary>
internal static class NativeEngine
{
    private const string Library = "tgws_core";

    /// <summary>Log levels the callback uses: 0 debug, 1 info, 2 warn, 3 error.</summary>
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    internal delegate void LogCallback(int level, IntPtr message, IntPtr user);

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    internal static extern IntPtr tgws_core_version();

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    internal static extern IntPtr tgws_engine_start(IntPtr configJson, LogCallback? callback, IntPtr user);

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    internal static extern int tgws_engine_stop(IntPtr handle);

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    internal static extern int tgws_engine_free(IntPtr handle);

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    internal static extern IntPtr tgws_engine_status(IntPtr handle);

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    internal static extern IntPtr tgws_engine_link(IntPtr handle);

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    internal static extern IntPtr tgws_engine_logs(IntPtr handle, int lastN);

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    internal static extern void tgws_string_free(IntPtr text);
}

/// <summary>The snapshot <c>tgws_engine_status</c> returns.</summary>
internal sealed record RustEngineStatus(
    [property: JsonPropertyName("running")] bool Running,
    [property: JsonPropertyName("port")] int Port,
    [property: JsonPropertyName("uptime_seconds")] long UptimeSeconds,
    [property: JsonPropertyName("connections")] long Connections,
    [property: JsonPropertyName("connections_closed")] long ConnectionsClosed,
    [property: JsonPropertyName("sessions")] long Sessions,
    [property: JsonPropertyName("errors")] long Errors,
    [property: JsonPropertyName("bad_handshakes")] long BadHandshakes,
    [property: JsonPropertyName("ws_sessions")] long WsSessions,
    [property: JsonPropertyName("tcp_fallbacks")] long TcpFallbacks,
    [property: JsonPropertyName("masked")] long Masked,
    [property: JsonPropertyName("bytes_up")] long BytesUp,
    [property: JsonPropertyName("bytes_down")] long BytesDown)
{
    /// <summary>Connections that are still open; the status has no explicit field for them.</summary>
    public long ActiveConnections => Math.Max(0, Connections - ConnectionsClosed);
}

/// <summary>
/// One Rust engine handle. The callback delegate is kept in a field for as long as the handle lives: without that
/// reference the GC would collect it and an engine thread would jump into a dead trampoline.
/// </summary>
internal sealed class RustEngine : IDisposable
{
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };

    private readonly object _gate = new();
    private readonly Action<int, string> _onLog;
    private NativeEngine.LogCallback? _callback;
    private IntPtr _handle;
    private bool _disposed;

    public RustEngine(Action<int, string> onLog)
    {
        _onLog = onLog;
    }

    /// <summary>The message of the last error-severity callback, used to explain a failed start.</summary>
    public string? LastError { get; private set; }

    public bool IsRunning { get; private set; }

    public static string Version()
    {
        IntPtr pointer = NativeEngine.tgws_core_version();
        return pointer == IntPtr.Zero ? "" : Marshal.PtrToStringUTF8(pointer) ?? "";
    }

    /// <summary>Starts the engine from a UTF-8 JSON config. Returns false when the core refused it.</summary>
    public bool Start(string configJson)
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_handle != IntPtr.Zero) throw new InvalidOperationException("движок уже запущен");

            LastError = null;
            _callback = OnNativeLog;
            IntPtr text = Marshal.StringToCoTaskMemUTF8(configJson);
            try
            {
                _handle = NativeEngine.tgws_engine_start(text, _callback, IntPtr.Zero);
            }
            finally
            {
                Marshal.FreeCoTaskMem(text);
            }

            IsRunning = _handle != IntPtr.Zero;
            return IsRunning;
        }
    }

    /// <summary>Stops the listener but keeps the handle for the final status/log read.</summary>
    public void Stop()
    {
        lock (_gate)
        {
            if (_handle == IntPtr.Zero) return;
            NativeEngine.tgws_engine_stop(_handle);
            IsRunning = false;
        }
    }

    public RustEngineStatus? Status()
    {
        lock (_gate)
        {
            if (_handle == IntPtr.Zero) return null;
            return ReadStatus(NativeEngine.tgws_engine_status(_handle));
        }
    }

    public string? Link()
    {
        lock (_gate)
        {
            if (_handle == IntPtr.Zero) return null;
            return TakeString(NativeEngine.tgws_engine_link(_handle));
        }
    }

    public IReadOnlyList<string> Logs(int lastN)
    {
        lock (_gate)
        {
            if (_handle == IntPtr.Zero) return [];
            string? json = TakeString(NativeEngine.tgws_engine_logs(_handle, lastN));
            if (string.IsNullOrEmpty(json)) return [];
            try
            {
                return JsonSerializer.Deserialize<string[]>(json, JsonOptions) ?? [];
            }
            catch (JsonException)
            {
                return [];
            }
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            if (_handle != IntPtr.Zero)
            {
                NativeEngine.tgws_engine_stop(_handle);
                NativeEngine.tgws_engine_free(_handle);
                _handle = IntPtr.Zero;
            }
            // The handle is gone, so no engine thread can call back any more; let the delegate be collected.
            _callback = null;
            IsRunning = false;
        }
    }

    private void OnNativeLog(int level, IntPtr message, IntPtr user)
    {
        string line = message == IntPtr.Zero ? "" : Marshal.PtrToStringUTF8(message) ?? "";
        if (level >= 3) LastError = line;
        try
        {
            _onLog(level, line);
        }
        catch
        {
            // A logging subscriber must never take an engine thread down through the C boundary.
        }
    }

    private static RustEngineStatus? ReadStatus(IntPtr pointer)
    {
        string? json = TakeString(pointer);
        if (string.IsNullOrEmpty(json)) return null;
        try
        {
            return JsonSerializer.Deserialize<RustEngineStatus>(json, JsonOptions);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string? TakeString(IntPtr pointer)
    {
        if (pointer == IntPtr.Zero) return null;
        try
        {
            return Marshal.PtrToStringUTF8(pointer);
        }
        finally
        {
            NativeEngine.tgws_string_free(pointer);
        }
    }
}
