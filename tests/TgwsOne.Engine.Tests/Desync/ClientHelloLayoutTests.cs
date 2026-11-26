using System.Buffers.Binary;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Text;
using TgwsOne.Engine.Desync;

namespace TgwsOne.Engine.Tests.Desync;

public sealed class ClientHelloLayoutTests
{
    [Theory]
    [InlineData(false, false, false)]
    [InlineData(true, false, false)]
    [InlineData(false, true, false)]
    [InlineData(true, true, true)]
    [InlineData(false, true, true)]
    public void ParsesSyntheticClientHello(bool tls13, bool sessionId, bool grease)
    {
        byte[] hello = DesyncTestData.ClientHello(tls13: tls13, sessionId: sessionId, grease: grease);

        Assert.True(ClientHelloLayout.TryParse(hello, out ClientHelloLayout layout));
        Assert.True(layout.HasSni);
        Assert.Equal(DesyncTestData.Host, layout.Sni);
        Assert.Equal(hello.Length, layout.Length);
        Assert.Equal(hello.Length, layout.RecordEnd);

        int start = Assert.IsType<int>(layout.Resolve(new SniStart()));
        int end = Assert.IsType<int>(layout.Resolve(new SniEnd()));
        Assert.Equal(DesyncTestData.Host, Encoding.ASCII.GetString(hello, start, end - start));
        Assert.Equal(start, layout.Resolve(new SniStart(0)));
        Assert.Equal(start + 2, layout.Resolve(new SniStart(2)));
        Assert.Equal(end - 1, layout.Resolve(new SniEnd(-1)));
        Assert.Equal((start + end) / 2, layout.Resolve(new SniMid()));
        Assert.Equal(start + 7, layout.Resolve(new SldMid()));
        Assert.Equal(hello.Length, layout.Resolve(new FromEnd(0)));
        Assert.Equal(hello.Length - 10, layout.Resolve(new FromEnd(10)));
        Assert.Equal(4, layout.Resolve(new Absolute(4)));
    }

    [Fact]
    public void ParsesTls13SupportedVersionsAndKeepsRecordVersion()
    {
        byte[] hello = DesyncTestData.ClientHello(tls13: true);

        Assert.True(ClientHelloLayout.TryParse(hello, out ClientHelloLayout layout));
        Assert.Equal(0x01, layout.RecordVersionMinor);
        Assert.Equal(DesyncTestData.Host, layout.Sni);
    }

    [Fact]
    public void ParsesHelloWithoutSni()
    {
        byte[] hello = DesyncTestData.ClientHello(host: null);

        Assert.True(ClientHelloLayout.TryParse(hello, out ClientHelloLayout layout));
        Assert.False(layout.HasSni);
        Assert.Null(layout.Sni);
        Assert.Null(layout.Resolve(new SniStart()));
        Assert.Null(layout.Resolve(new SniEnd()));
        Assert.Null(layout.Resolve(new SniMid()));
        Assert.Null(layout.Resolve(new SldMid()));
        Assert.Equal(0, layout.Resolve(new Absolute(0)));
    }

    [Fact]
    public void ParsesSniWithoutDotAsWholeName()
    {
        byte[] hello = DesyncTestData.ClientHello(host: "localhost");

        Assert.True(ClientHelloLayout.TryParse(hello, out ClientHelloLayout layout));
        int start = layout.Resolve(new SniStart())!.Value;
        int end = layout.Resolve(new SniEnd())!.Value;
        // Without a dot the whole host is the second-level domain, so its middle is the host's middle.
        Assert.Equal((start + end) / 2, layout.Resolve(new SldMid()));
    }

    [Fact]
    public void RejectsEmptyBuffer()
    {
        Assert.False(ClientHelloLayout.TryParse([], out _));
    }

    [Fact]
    public void RejectsNonHandshakeRecord()
    {
        byte[] hello = DesyncTestData.ClientHello(recordType: 0x17);
        Assert.False(ClientHelloLayout.TryParse(hello, out _));
    }

    [Fact]
    public void RejectsNonClientHelloHandshake()
    {
        byte[] hello = DesyncTestData.ClientHello(handshakeType: 0x02);
        Assert.False(ClientHelloLayout.TryParse(hello, out _));
    }

    [Fact]
    public void RejectsRecordLongerThanBuffer()
    {
        byte[] hello = DesyncTestData.ClientHello(recordLength: 0x7fff);
        Assert.False(ClientHelloLayout.TryParse(hello, out _));
    }

    [Fact]
    public void RejectsTruncatedBuffer()
    {
        byte[] hello = DesyncTestData.ClientHello();
        Assert.False(ClientHelloLayout.TryParse(hello[..^1], out _));
    }

    [Fact]
    public void RejectsClientHelloSpanningTwoRecords()
    {
        byte[] hello = DesyncTestData.ClientHello();
        int recordLength = BinaryPrimitives.ReadUInt16BigEndian(hello.AsSpan(3, 2));
        byte[] twoRecords = DesyncTestData.SplitRecord(hello, recordLength / 2);

        Assert.False(ClientHelloLayout.TryParse(twoRecords, out _));
    }

    [Fact]
    public void RejectsTruncatedSessionIdVector()
    {
        byte[] hello = DesyncTestData.ClientHello(sessionId: true);
        int sessionLengthOffset = 9 + 2 + 32;
        hello[sessionLengthOffset] = 0xff;

        Assert.False(ClientHelloLayout.TryParse(hello, out _));
    }

    [Fact]
    public void RejectsTruncatedExtensions()
    {
        byte[] hello = DesyncTestData.ClientHello();
        int recordLength = BinaryPrimitives.ReadUInt16BigEndian(hello.AsSpan(3, 2));
        Assert.False(ClientHelloLayout.TryParse(hello[..(5 + recordLength - 1)], out _));
    }

    [Fact]
    public async Task ParsesRealClientHelloFromSslStream()
    {
        const string targetHost = "desync-real.example.test";
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        try
        {
            int port = ((IPEndPoint)listener.LocalEndpoint).Port;
            Task<byte[]> capture = Task.Run(async () =>
            {
                using TcpClient accepted = await listener.AcceptTcpClientAsync(timeout.Token);
                NetworkStream stream = accepted.GetStream();
                byte[] header = new byte[5];
                await stream.ReadExactlyAsync(header, timeout.Token);
                int length = (header[3] << 8) | header[4];
                byte[] body = new byte[length];
                await stream.ReadExactlyAsync(body, timeout.Token);
                return new[] { header, body }.SelectMany(part => part).ToArray();
            }, timeout.Token);

            using var client = new TcpClient();
            await client.ConnectAsync(IPAddress.Loopback, port, timeout.Token);
            using var ssl = new SslStream(client.GetStream(), leaveInnerStreamOpen: false,
                userCertificateValidationCallback: (_, _, _, _) => true);
            try
            {
                await ssl.AuthenticateAsClientAsync(
                    new SslClientAuthenticationOptions { TargetHost = targetHost }, timeout.Token);
            }
            catch (Exception ex) when (ex is IOException or AuthenticationException or OperationCanceledException)
            {
                // The capture side closes without a ServerHello; a failed handshake is the expected outcome.
            }

            byte[] firstFlight = await capture.WaitAsync(timeout.Token);
            Assert.True(ClientHelloLayout.TryParse(firstFlight, out ClientHelloLayout layout));
            Assert.Equal(targetHost, layout.Sni);
        }
        finally
        {
            listener.Stop();
        }
    }
}
