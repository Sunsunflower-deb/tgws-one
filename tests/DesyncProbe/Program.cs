// Root-stend probe for the desync layer (plan E2.6): one outgoing TLS connection through EgressSocket with a fixed
// strategy, then exactly one JSON line on stdout. tests/root/netns-desync.sh parses that line and checks the captures.
//
//   --host <name-or-ip>   where to connect (the server in the netns)
//   --port <n>            remote port, 443 by default
//   --strategy <id>       none | split | disorder | fake | oob | tlsrec
//   --sni <name>          TLS name to send in the ClientHello, the host by default
//   --timeout <ms>        handshake timeout, 8000 by default
//   --connect-timeout <ms> connect timeout, 10000 by default
//
// The tap stream between SslStream and DesyncStream records the first flight, so the JSON can also report the
// resolved cut offset, the ClientHello length and its SNI: the capture checks do not have to guess where a
// position-based step landed.
using System.Diagnostics;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Text.Json;
using TgwsOne.Engine.Desync;

namespace DesyncProbe;

internal static class Program
{
    private static async Task<int> Main(string[] args)
    {
        if (!TryParse(args, out Options options, out string? argumentError))
        {
            Console.Error.WriteLine(argumentError);
            return 2;
        }

        if (!TryBuildStrategy(options.StrategyId, out DesyncStrategy? strategy,
                              out DesyncPosition? cutPosition, out string? strategyError))
        {
            Console.Error.WriteLine(strategyError);
            return 2;
        }

        var stopwatch = Stopwatch.StartNew();
        bool ok = false;
        string? error = null;
        EgressConnection? connection = null;
        TapStream? tap = null;
        SslStream? tls = null;
        try
        {
            connection = await EgressSocket.ConnectAsync(
                options.Host, options.Port, strategy,
                timeout: TimeSpan.FromMilliseconds(options.ConnectTimeoutMs),
                log: line => Console.Error.WriteLine(line)).ConfigureAwait(false);

            tap = new TapStream(connection.Stream);
            tls = new SslStream(tap, leaveInnerStreamOpen: true, (_, _, _, _) => true);
            using var timeout = new CancellationTokenSource(options.TimeoutMs);
            await tls.AuthenticateAsClientAsync(new SslClientAuthenticationOptions
            {
                TargetHost = options.Sni,
                EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13,
            }, timeout.Token).ConfigureAwait(false);
            ok = true;
        }
        catch (Exception ex)
        {
            error = $"{ex.GetType().Name}: {ex.Message}";
        }
        stopwatch.Stop();

        long? retransmits = null;
        int? systemTtl = null;
        if (connection is not null)
        {
            systemTtl = connection.SystemTtl;
            try
            {
                retransmits = SocketTricks.ForCurrentPlatform().ReadRetransmitCount(connection.Socket);
            }
            catch (Exception)
            {
                // TCP_INFO is diagnostics only; a platform without it must still print the JSON line.
            }
        }

        int? cut = null;
        int? helloLength = null;
        string? helloSni = null;
        if (tap?.FirstWrite is { Length: > 0 } firstFlight)
        {
            helloLength = firstFlight.Length;
            if (ClientHelloLayout.TryParse(firstFlight, out ClientHelloLayout layout))
            {
                helloSni = layout.Sni;
                if (cutPosition is not null) cut = layout.Resolve(cutPosition);
            }
        }

        var result = new Dictionary<string, object?>
        {
            ["ok"] = ok,
            ["ms"] = stopwatch.ElapsedMilliseconds,
            ["retransmits"] = retransmits,
            ["strategy"] = options.StrategyId,
            ["sni"] = options.Sni,
            ["system_ttl"] = systemTtl,
            ["socket_family"] = connection?.Socket.AddressFamily.ToString(),
            ["cut"] = cut,
            ["hello_len"] = helloLength,
            ["hello_sni"] = helloSni,
            ["error"] = error,
        };
        Console.WriteLine(JsonSerializer.Serialize(result));

        if (tls is not null) await tls.DisposeAsync().ConfigureAwait(false);
        if (connection is not null) await connection.DisposeAsync().ConfigureAwait(false);
        return ok ? 0 : 1;
    }

    private static bool TryBuildStrategy(string id, out DesyncStrategy? strategy, out DesyncPosition? cut,
                                         out string? error)
    {
        error = null;
        switch (id)
        {
            case "none":
                strategy = null;
                cut = null;
                return true;

            case "split":
            {
                // Cut inside the SNI, then wait: two separate writes alone may still be coalesced into one segment.
                DesyncPosition position = new SniStart(2);
                cut = position;
                strategy = new DesyncStrategy(id, [new Split(position), new Pause(TimeSpan.FromMilliseconds(40))]);
                return true;
            }

            case "disorder":
            {
                DesyncPosition position = new SniMid();
                cut = position;
                strategy = new DesyncStrategy(id, [new Disorder(position)]);
                return true;
            }

            case "fake":
            {
                // The replaced range has to include the whole SNI: the retransmitted first part must still parse as
                // the real hello. TTL 1 dies on the first hop, which in this stend is the router namespace.
                DesyncPosition position = new SniEnd(20);
                cut = position;
                strategy = new DesyncStrategy(id,
                    [new Fake(position, Ttl: 1, Sni: FakeClientHello.DefaultSni)]);
                return true;
            }

            case "oob":
            {
                DesyncPosition position = new SniMid();
                cut = position;
                strategy = new DesyncStrategy(id, [new Oob(position)]);
                return true;
            }

            case "tlsrec":
            {
                DesyncPosition position = new SniMid();
                cut = position;
                strategy = new DesyncStrategy(id, [new TlsRec(position)]);
                return true;
            }

            default:
                strategy = null;
                cut = null;
                error = $"unknown strategy '{id}' (expected none, split, disorder, fake, oob or tlsrec)";
                return false;
        }
    }

    private static bool TryParse(string[] args, out Options options, out string? error)
    {
        string host = "";
        int port = 443;
        string strategy = "none";
        string? sni = null;
        int timeoutMs = 8000;
        int connectTimeoutMs = 10000;

        for (int index = 0; index < args.Length; index++)
        {
            string name = args[index];
            switch (name)
            {
                case "--host":
                    if (!TryNext(args, ref index, out host)) { options = default!; error = "--host needs a value"; return false; }
                    break;
                case "--port":
                    if (!TryNext(args, ref index, out string portText)) { options = default!; error = "--port needs a value"; return false; }
                    if (!int.TryParse(portText, out port) || port is < 1 or > 65535)
                    { options = default!; error = $"invalid --port '{portText}'"; return false; }
                    break;
                case "--strategy":
                    if (!TryNext(args, ref index, out strategy)) { options = default!; error = "--strategy needs a value"; return false; }
                    break;
                case "--sni":
                    if (!TryNext(args, ref index, out string sniValue)) { options = default!; error = "--sni needs a value"; return false; }
                    sni = sniValue;
                    break;
                case "--timeout":
                    if (!TryNext(args, ref index, out string timeoutText)) { options = default!; error = "--timeout needs a value"; return false; }
                    if (!int.TryParse(timeoutText, out timeoutMs) || timeoutMs < 1)
                    { options = default!; error = $"invalid --timeout '{timeoutText}'"; return false; }
                    break;
                case "--connect-timeout":
                    if (!TryNext(args, ref index, out string connectText)) { options = default!; error = "--connect-timeout needs a value"; return false; }
                    if (!int.TryParse(connectText, out connectTimeoutMs) || connectTimeoutMs < 1)
                    { options = default!; error = $"invalid --connect-timeout '{connectText}'"; return false; }
                    break;
                default:
                    options = default!;
                    error = $"unknown argument '{name}'";
                    return false;
            }
        }

        if (string.IsNullOrEmpty(host))
        {
            options = default!;
            error = "--host is required";
            return false;
        }

        options = new Options(host, port, strategy, sni ?? host, timeoutMs, connectTimeoutMs);
        error = null;
        return true;
    }

    private static bool TryNext(string[] args, ref int index, out string value)
    {
        if (index + 1 >= args.Length)
        {
            value = "";
            return false;
        }

        value = args[++index];
        return true;
    }

    private sealed record Options(string Host, int Port, string StrategyId, string Sni, int TimeoutMs,
                                  int ConnectTimeoutMs);

    /// <summary>
    /// Passes every read and write through to the desync stream and keeps a copy of the first write, which is the
    /// ClientHello. It does not own the inner stream: the connection closes it.
    /// </summary>
    private sealed class TapStream : Stream
    {
        private readonly Stream _inner;
        private byte[]? _firstWrite;
        private int _firstWriteSeen;

        public TapStream(Stream inner) => _inner = inner;

        public byte[]? FirstWrite => _firstWrite;

        public override bool CanRead => _inner.CanRead;
        public override bool CanSeek => false;
        public override bool CanWrite => _inner.CanWrite;

        public override long Length => throw new NotSupportedException();
        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override int Read(byte[] buffer, int offset, int count) => _inner.Read(buffer, offset, count);
        public override int Read(Span<byte> buffer) => _inner.Read(buffer);

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
            => _inner.ReadAsync(buffer, cancellationToken);

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
            => _inner.ReadAsync(buffer, offset, count, cancellationToken);

        public override void Flush() => _inner.Flush();
        public override Task FlushAsync(CancellationToken cancellationToken) => _inner.FlushAsync(cancellationToken);
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count)
            => Write(buffer.AsSpan(offset, count));

        public override void Write(ReadOnlySpan<byte> buffer)
        {
            Capture(buffer);
            _inner.Write(buffer);
        }

        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer,
                                             CancellationToken cancellationToken = default)
        {
            Capture(buffer.Span);
            return _inner.WriteAsync(buffer, cancellationToken);
        }

        public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
            => WriteAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

        private void Capture(ReadOnlySpan<byte> buffer)
        {
            if (Interlocked.Exchange(ref _firstWriteSeen, 1) != 0) return;
            _firstWrite = buffer.ToArray();
        }

        protected override void Dispose(bool disposing)
        {
            // The EgressConnection owns the inner stream; SslStream is constructed with leaveInnerStreamOpen anyway.
            base.Dispose(disposing);
        }
    }
}
