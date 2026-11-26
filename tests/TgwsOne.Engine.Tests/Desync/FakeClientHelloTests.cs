using TgwsOne.Engine.Desync;

namespace TgwsOne.Engine.Tests.Desync;

public sealed class FakeClientHelloTests
{
    private static readonly byte[] Tls13SupportedVersions = [0x00, 0x2b, 0x00, 0x03, 0x02, 0x03, 0x04];

    [Theory]
    [InlineData(517)]
    [InlineData(1024)]
    [InlineData(4096)]
    public void Build_ProducesTheExactRequestedLength(int length)
    {
        byte[] hello = FakeClientHello.Build("fake.example", length, seed: 42);

        Assert.Equal(length, hello.Length);
        Assert.True(ClientHelloLayout.TryParse(hello, out ClientHelloLayout layout));
        Assert.Equal(length, layout.Length);
        Assert.True(layout.HasSni);
        Assert.Equal("fake.example", layout.Sni);
        Assert.True(Contains(hello, Tls13SupportedVersions),
            "the fake hello does not advertise TLS 1.3 in supported_versions");
    }

    [Fact]
    public void Build_UsesGoogleAsTheDefaultSni()
    {
        byte[] hello = FakeClientHello.Build(null, 517, seed: 7);

        Assert.True(ClientHelloLayout.TryParse(hello, out ClientHelloLayout layout));
        Assert.Equal(FakeClientHello.DefaultSni, layout.Sni);
        Assert.Equal("www.google.com", layout.Sni);
    }

    [Fact]
    public void Build_IsDeterministicForOneSeedAndVariesWithAnother()
    {
        byte[] first = FakeClientHello.Build("fake.example", 517, seed: 1234);
        byte[] same = FakeClientHello.Build("fake.example", 517, seed: 1234);
        byte[] different = FakeClientHello.Build("fake.example", 517, seed: 1235);

        Assert.Equal(first, same);
        Assert.NotEqual(first, different);
    }

    [Fact]
    public void Build_RejectsALengthThatCannotHoldAClientHello()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => FakeClientHello.Build("fake.example", 10, seed: 1));
        Assert.Throws<ArgumentOutOfRangeException>(() => FakeClientHello.Build("fake.example", 0, seed: 1));
    }

    private static bool Contains(byte[] haystack, byte[] needle) => haystack.AsSpan().IndexOf(needle) >= 0;
}
