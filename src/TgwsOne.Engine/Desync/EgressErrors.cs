using System.Net.Sockets;
using System.Security.Authentication;
using TgwsOne.Engine.Transport;

namespace TgwsOne.Engine.Desync;

/// <summary>Where in the connection attempt an egress failure happened.</summary>
public enum EgressStage
{
    /// <summary>TCP connect (DNS, dialing, the timeout around it).</summary>
    Connect,

    /// <summary>The TLS handshake over the connected stream.</summary>
    Tls,

    /// <summary>The HTTP upgrade that turns the connection into a WebSocket.</summary>
    Upgrade,

    /// <summary>Anything after the transport is up, or an error that does not name a stage.</summary>
    Io,
}

/// <summary>An egress failure that already knows its stage and <see cref="EgressFailure"/> kind.</summary>
public sealed class EgressException(EgressFailure failure, EgressStage stage, Exception? inner = null)
    : Exception($"egress {stage} attempt failed: {failure}", inner)
{
    public EgressFailure Failure { get; } = failure;

    public EgressStage Stage { get; } = stage;
}

/// <summary>
/// The single place that maps an exception to an <see cref="EgressFailure"/>. The stage matters: the same
/// <see cref="TimeoutException"/> is a connect timeout during dialing and a TLS timeout during the handshake, and the
/// controller needs that distinction to judge a strategy. The overload without a stage guesses it from the exception
/// type so callers that only have the exception still get a sensible answer.
/// </summary>
public static class EgressErrors
{
    public static (EgressFailure Failure, EgressStage Stage) Classify(Exception exception)
    {
        ArgumentNullException.ThrowIfNull(exception);
        if (exception is EgressException egress) return (egress.Failure, egress.Stage);

        EgressStage stage = exception switch
        {
            WsHandshakeException => EgressStage.Upgrade,
            AuthenticationException => EgressStage.Tls,
            SocketException or TimeoutException or OperationCanceledException => EgressStage.Connect,
            _ => EgressStage.Io,
        };
        return Classify(exception, stage);
    }

    public static (EgressFailure Failure, EgressStage Stage) Classify(Exception exception, EgressStage stage)
    {
        ArgumentNullException.ThrowIfNull(exception);
        if (exception is EgressException egress) return (egress.Failure, egress.Stage);

        EgressFailure failure = stage switch
        {
            EgressStage.Connect => ClassifyConnect(exception),
            EgressStage.Tls => ClassifyTls(exception),
            // An HTTP status that is not 101 is the only structured refusal at this stage; everything else is a
            // transport error while writing the request or reading the response.
            EgressStage.Upgrade => exception is WsHandshakeException
                ? EgressFailure.UpgradeRejected
                : EgressFailure.Io,
            _ => EgressFailure.Io,
        };
        return (failure, stage);
    }

    private static EgressFailure ClassifyConnect(Exception exception)
    {
        foreach (Exception current in Chain(exception))
        {
            switch (current)
            {
                case EgressException egress:
                    return egress.Failure;

                // The linked timeout token surfaces as cancellation, not as SocketException; both mean the dial did
                // not finish in time from the route's point of view.
                case TimeoutException or OperationCanceledException:
                    return EgressFailure.ConnectTimeout;

                case SocketException socket:
                    return socket.SocketErrorCode switch
                    {
                        SocketError.TimedOut => EgressFailure.ConnectTimeout,
                        SocketError.ConnectionRefused or SocketError.HostUnreachable
                            or SocketError.NetworkUnreachable or SocketError.HostDown
                            or SocketError.NetworkDown => EgressFailure.ConnectRefused,
                        _ => EgressFailure.Io,
                    };
            }
        }
        return EgressFailure.Io;
    }

    private static EgressFailure ClassifyTls(Exception exception)
    {
        bool sawAuthenticationFailure = false;
        foreach (Exception current in Chain(exception))
        {
            switch (current)
            {
                case EgressException egress:
                    return egress.Failure;

                case TimeoutException or OperationCanceledException:
                    return EgressFailure.TlsTimeout;

                case SocketException socket:
                    if (socket.SocketErrorCode == SocketError.TimedOut) return EgressFailure.TlsTimeout;
                    if (socket.SocketErrorCode is SocketError.ConnectionReset or SocketError.ConnectionAborted
                        or SocketError.ConnectionRefused or SocketError.NetworkReset
                        or SocketError.NetworkDown) return EgressFailure.TlsReset;
                    break;

                case AuthenticationException:
                    sawAuthenticationFailure = true;
                    break;

                case IOException io:
                    // .NET reports a peer reset during the handshake as a plain IOException with these words; an
                    // alert arrives as an AuthenticationException, which is the fallback below.
                    string message = io.Message;
                    if (Contains(message, "reset") || Contains(message, "forcibly closed") ||
                        Contains(message, "broken pipe") || Contains(message, "unexpected eof") ||
                        Contains(message, "closed the transport stream")) return EgressFailure.TlsReset;
                    if (Contains(message, "alert")) return EgressFailure.TlsAlert;
                    break;
            }
        }

        // A TLS handshake that fails for a reason we cannot name more precisely is the peer rejecting it, which is
        // what the controller can act on (a different strategy may be accepted).
        return sawAuthenticationFailure ? EgressFailure.TlsAlert : EgressFailure.Io;
    }

    private static bool Contains(string text, string value)
        => text.Contains(value, StringComparison.OrdinalIgnoreCase);

    /// <summary>The exception and everything it wraps, including every branch of an <see cref="AggregateException"/>.</summary>
    private static IEnumerable<Exception> Chain(Exception exception)
    {
        var pending = new Stack<Exception>();
        pending.Push(exception);
        while (pending.Count > 0)
        {
            Exception current = pending.Pop();
            yield return current;

            if (current is AggregateException aggregate)
            {
                foreach (Exception inner in aggregate.InnerExceptions) pending.Push(inner);
            }
            else if (current.InnerException is not null)
            {
                pending.Push(current.InnerException);
            }
        }
    }
}
