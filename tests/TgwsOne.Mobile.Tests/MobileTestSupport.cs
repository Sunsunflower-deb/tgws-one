using System.Net;
using System.Net.Sockets;
using TgwsOne.Core;

namespace TgwsOne.Mobile.Tests;

/// <summary>A throwaway <c>files/</c> directory: the INI, app log and proxy log land under one temp path.</summary>
internal sealed class TempFiles : IDisposable
{
    public string Root { get; }
    public MobileStorage Storage { get; }

    public TempFiles()
    {
        Root = Path.Combine(Path.GetTempPath(), "tgwsone-mobile-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Root);
        Storage = MobileStorage.For(Root);
    }

    public void Configure(Action<IniFile> edit)
    {
        IniFile ini = IniFile.Load(Storage.IniPath);
        edit(ini);
        ini.Save();
    }

    public void Dispose()
    {
        try { Directory.Delete(Root, recursive: true); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }
}

internal static class MobilePorts
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
