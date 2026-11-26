// Record-driver shim for rust-anything (RT3).  The original takes `IPAddress`;
// the bridge needs strings.  `BuildEe` calls the real
// `EngineConfig.TelegramLink` (the record driver adds TgwsOne.Engine), keeping
// the wildcard/socket `GetLinkHost` path out of the recorded cases.
// Not part of TgwsOne.slnx and not compiled into any product assembly.
using System.Net;
using TgwsOne.Engine;

namespace TgwsOne.Core;

public static class TelegramLinkShims
{
    public static string ConnectAddress(string listen)
        => TelegramLink.ConnectAddress(IPAddress.Parse(listen)).ToString();

    public static string Build(string host, int port, string secret)
        => TelegramLink.Build(IPAddress.Parse(host), port, secret);

    public static string BuildEe(string host, int port, string secret, string fakeTlsDomain)
        => new EngineConfig { Host = host, Port = port, Secret = secret, FakeTlsDomain = fakeTlsDomain }.TelegramLink();
}
