using System.Net;

namespace TgwsOne.Core.Tests;

public class TelegramLinkTests
{
    private const string Secret = "0123456789abcdef0123456789abcdef";

    [Fact]
    public void Link_Loopback()
        => Assert.Equal($"tg://proxy?server=127.0.0.1&port=1443&secret=dd{Secret}",
            TelegramLink.Build(IPAddress.Parse("127.0.0.1"), 1443, Secret));

    [Fact]
    public void Link_AnyMapsToLoopback()
        => Assert.Equal($"tg://proxy?server=127.0.0.1&port=2080&secret=dd{Secret}",
            TelegramLink.Build(IPAddress.Any, 2080, Secret));

    [Fact]
    public void Link_IPv6AnyMapsToIPv6Loopback()
        => Assert.Equal($"tg://proxy?server=%3A%3A1&port=1443&secret=dd{Secret}",
            TelegramLink.Build(IPAddress.IPv6Any, 1443, Secret));

    [Fact]
    public void Link_OtherIPv6_EscapedNoBrackets()
    {
        string link = TelegramLink.Build(IPAddress.Parse("2001:db8::1"), 1443, Secret);

        Assert.Equal($"tg://proxy?server=2001%3Adb8%3A%3A1&port=1443&secret=dd{Secret}", link);
        Assert.DoesNotContain("[", link);
    }

    [Fact]
    public void ConnectAddress_MapsWildcardsAndKeepsTheRest()
    {
        Assert.Equal(IPAddress.Loopback, TelegramLink.ConnectAddress(IPAddress.Any));
        Assert.Equal(IPAddress.IPv6Loopback, TelegramLink.ConnectAddress(IPAddress.IPv6Any));
        Assert.Equal(IPAddress.Parse("192.168.1.5"), TelegramLink.ConnectAddress(IPAddress.Parse("192.168.1.5")));
        Assert.Equal(IPAddress.IPv6Loopback, TelegramLink.ConnectAddress(IPAddress.IPv6Loopback));
    }
}
