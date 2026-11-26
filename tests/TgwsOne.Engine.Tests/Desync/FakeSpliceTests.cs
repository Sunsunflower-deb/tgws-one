using System.Net.Sockets;
using TgwsOne.Engine.Desync;

namespace TgwsOne.Engine.Tests.Desync;

public sealed class FakeSpliceTests
{
    [Fact]
    public async Task FakeSplice_SendsFakeLowersTtlAndKeepsTheOriginalForRetransmission()
    {
        byte[] hello = DesyncTestData.ClientHello(padding: 600);
        int cut = 517;
        var events = new List<string>();
        var tricks = new RecordingTricks(events);
        var inner = new RecordingStream(events);
        using var socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        var strategy = new DesyncStrategy("fake",
            [new Fake(new Absolute(cut), Ttl: 11, Sni: "fake.example")]);

        using var stream = new DesyncStream(inner, socket, tricks, systemTtl: 64, strategy);
        await stream.WriteAsync(hello);

        string[] expectedEvents =
        [
            "ttl:11",
            $"send:{cut}",
            "ttl:64",
            $"write:{hello.Length - cut}",
        ];
        Assert.Equal(expectedEvents, events);

        Assert.NotNull(tricks.LastSent);
        Assert.Equal(cut, tricks.LastSent!.Length);
        Assert.True(ClientHelloLayout.TryParse(tricks.LastSent, out ClientHelloLayout fakeLayout));
        Assert.Equal("fake.example", fakeLayout.Sni);

        // Only the suffix after the fake is written normally: the replaced range must not hit the stream twice.
        Assert.Equal(hello.AsSpan(cut).ToArray(), inner.ToArray());

        byte[][] retained = stream.FakeSender.SnapshotBuffers();
        Assert.Single(retained);
        Assert.Equal(hello.AsSpan(0, cut).ToArray(), retained[0]);
    }

    [Fact]
    public async Task FakeSplice_FailureWritesTheRealBytesExactlyOnce()
    {
        byte[] hello = DesyncTestData.ClientHello(padding: 600);
        int cut = 517;
        var events = new List<string>();
        var logs = new List<string>();
        var tricks = new RecordingTricks(events) { SpliceResult = false };
        var inner = new RecordingStream(events);
        using var socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        var strategy = new DesyncStrategy("fake",
            [new Fake(new Absolute(cut), Ttl: 11, Sni: "fake.example")]);

        using var stream = new DesyncStream(inner, socket, tricks, systemTtl: 64, strategy, logs.Add);
        await stream.WriteAsync(hello);

        Assert.Equal(hello, inner.ToArray());
        Assert.Empty(stream.FakeSender.SnapshotBuffers());
        Assert.Contains(logs, line => line.Contains("fake splice failed", StringComparison.Ordinal));
    }

    [Fact]
    public async Task FakeSplice_UsesGoogleAndTheDefaultTtlWhenTheStepNamesNeither()
    {
        byte[] hello = DesyncTestData.ClientHello(host: null, padding: 600);
        int cut = 517;
        var events = new List<string>();
        var tricks = new RecordingTricks(events);
        var inner = new RecordingStream(events);
        using var socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        var strategy = new DesyncStrategy("fake", [new Fake(new Absolute(cut), Ttl: null, Sni: null)]);

        using var stream = new DesyncStream(inner, socket, tricks, systemTtl: 64, strategy);
        await stream.WriteAsync(hello);

        Assert.Equal($"ttl:{FakeSpliceSender.DefaultFakeTtl}", events[0]);
        Assert.NotNull(tricks.LastSent);
        Assert.True(ClientHelloLayout.TryParse(tricks.LastSent!, out ClientHelloLayout fakeLayout));
        Assert.Equal(FakeClientHello.DefaultSni, fakeLayout.Sni);
    }

    [Fact]
    public async Task FakeSplice_AfterTlsRecUsesTheRebuiltBytesAsTheOriginal()
    {
        byte[] hello = DesyncTestData.ClientHello(padding: 600);
        byte[] rebuilt = DesyncTestData.SplitRecord(hello, payloadCut: 35);
        int cut = 517;
        var events = new List<string>();
        var tricks = new RecordingTricks(events);
        var inner = new RecordingStream(events);
        using var socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        var strategy = new DesyncStrategy("tlsrec-fake",
        [
            new TlsRec(new Absolute(40)),
            new Fake(new Absolute(cut), Ttl: 11, Sni: "fake.example"),
        ]);

        using var stream = new DesyncStream(inner, socket, tricks, systemTtl: 64, strategy);
        await stream.WriteAsync(hello);

        Assert.Equal(rebuilt.AsSpan(cut).ToArray(), inner.ToArray());
        byte[][] retained = stream.FakeSender.SnapshotBuffers();
        Assert.Single(retained);
        Assert.Equal(rebuilt.AsSpan(0, cut).ToArray(), retained[0]);
    }

    [Fact]
    public async Task Disorder_SendsTheFirstPartWithLowTtlThenTheRestWithTheSystemTtl()
    {
        byte[] hello = DesyncTestData.ClientHello();
        int cut = 20;
        var events = new List<string>();
        var tricks = new RecordingTricks(events);
        var inner = new RecordingStream(events);
        using var socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        var strategy = new DesyncStrategy("disorder", [new Disorder(new Absolute(cut))]);

        using var stream = new DesyncStream(inner, socket, tricks, systemTtl: 64, strategy);
        await stream.WriteAsync(hello);

        string[] expectedEvents =
        [
            "ttl:1",
            $"write:{cut}",
            "ttl:64",
            $"write:{hello.Length - cut}",
        ];
        Assert.Equal(expectedEvents, events);
        Assert.Equal(hello, inner.ToArray());
    }

    private sealed class RecordingTricks(List<string> events) : ISocketTricks
    {
        public bool SupportsLowTtl => true;
        public bool SupportsSpliceFake => true;
        public bool SupportsUrgent => true;
        public bool SpliceResult { get; init; } = true;
        public byte[]? LastSent { get; private set; }

        public int GetSystemTtl(Socket socket) => 64;

        public void SetTtl(Socket socket, int ttl) => events.Add($"ttl:{ttl}");

        public bool TrySendSpliced(Socket socket, ReadOnlySpan<byte> bytes)
        {
            events.Add($"send:{bytes.Length}");
            LastSent = bytes.ToArray();
            return SpliceResult;
        }

        public bool TrySendUrgent(Socket socket, ReadOnlySpan<byte> bytes) => false;

        public long? ReadRetransmitCount(Socket socket) => null;
    }

    private sealed class RecordingStream(List<string> events) : Stream
    {
        private readonly MemoryStream _inner = new();

        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => throw new NotSupportedException();
        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public byte[] ToArray() => _inner.ToArray();

        public override void Flush()
        {
        }

        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count)
            => Write(buffer.AsSpan(offset, count));

        public override void Write(ReadOnlySpan<byte> buffer)
        {
            events.Add($"write:{buffer.Length}");
            _inner.Write(buffer);
        }

        public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
            => WriteAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            events.Add($"write:{buffer.Length}");
            return _inner.WriteAsync(buffer, cancellationToken);
        }
    }
}
