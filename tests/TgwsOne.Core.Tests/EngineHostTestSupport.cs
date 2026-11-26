using System.Net;
using System.Net.Sockets;
using TgwsOne.Core;

namespace TgwsOne.Core.Tests;

/// <summary>A throwaway profile: everything the host writes lands under one temporary directory.</summary>
internal sealed class TempPaths : IDisposable
{
    public string Root { get; }
    public AppPaths Paths { get; }

    public TempPaths(string dataDir = "data")
    {
        Root = Path.Combine(Path.GetTempPath(), "tgwsone-host-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Root);
        var env = new Dictionary<string, string>
        {
            ["TGWS_DATA_DIR"] = Path.Combine(Root, dataDir),
            ["XDG_RUNTIME_DIR"] = Path.Combine(Root, "run"),
            ["LOCALAPPDATA"] = Path.Combine(Root, "local"),
        };
        Paths = AppPaths.For(OsKind.Linux, name => env.GetValueOrDefault(name), Path.Combine(Root, "app"),
            Path.Combine(Root, "home"));
    }

    public void Configure(Action<IniFile> edit)
    {
        Directory.CreateDirectory(Paths.DataDir);
        IniFile ini = IniFile.Load(Paths.Ini);
        edit(ini);
        ini.Save();
    }

    public void Dispose()
    {
        try { Directory.Delete(Root, recursive: true); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }
}

internal static class Ports
{
    public static int Free()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    public static bool Listening(int port)
    {
        try
        {
            using var client = new TcpClient();
            client.Connect(IPAddress.Loopback, port);
            return true;
        }
        catch (SocketException)
        {
            return false;
        }
    }

    public static async Task<bool> WaitForAsync(Func<bool> condition, TimeSpan timeout)
    {
        DateTime deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (condition()) return true;
            await Task.Delay(25).ConfigureAwait(false);
        }
        return condition();
    }
}
