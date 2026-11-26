using System.Net.Sockets;
using System.Security.Authentication;
using TgwsOne.Engine.Desync;
using TgwsOne.Engine.Transport;

namespace TgwsOne.Engine.Tests.Desync;

public sealed class EgressErrorsTests
{
    [Fact]
    public void Classify_MapsExceptionTypesToFailureAndStage()
    {
        AssertClassified(new SocketException((int)SocketError.ConnectionRefused),
            EgressFailure.ConnectRefused, EgressStage.Connect);
        AssertClassified(new SocketException((int)SocketError.NetworkUnreachable),
            EgressFailure.ConnectRefused, EgressStage.Connect);
        AssertClassified(new SocketException((int)SocketError.TimedOut),
            EgressFailure.ConnectTimeout, EgressStage.Connect);
        AssertClassified(new TimeoutException(), EgressFailure.ConnectTimeout, EgressStage.Connect);
        AssertClassified(new OperationCanceledException(), EgressFailure.ConnectTimeout, EgressStage.Connect);

        AssertClassified(new AuthenticationException("the handshake was rejected"),
            EgressFailure.TlsAlert, EgressStage.Tls);
        AssertClassified(new AuthenticationException("timed out", new TimeoutException()),
            EgressFailure.TlsTimeout, EgressStage.Tls);
        AssertClassified(new AuthenticationException("reset",
                new SocketException((int)SocketError.ConnectionReset)),
            EgressFailure.TlsReset, EgressStage.Tls);
        AssertClassified(new AuthenticationException("truncated",
                new IOException("Received an unexpected EOF")),
            EgressFailure.TlsReset, EgressStage.Tls);
        AssertClassified(new AuthenticationException("alert",
                new IOException("The peer sent a fatal alert")),
            EgressFailure.TlsAlert, EgressStage.Tls);

        AssertClassified(new WsHandshakeException(403, "HTTP/1.1 403 Forbidden"),
            EgressFailure.UpgradeRejected, EgressStage.Upgrade);
        AssertClassified(new IOException("the disk is on fire"), EgressFailure.Io, EgressStage.Io);
    }

    [Fact]
    public void Classify_WithAnExplicitStage_UsesThatStage()
    {
        AssertClassified(new TimeoutException(), EgressFailure.TlsTimeout, EgressStage.Tls,
            explicitStage: EgressStage.Tls);
        AssertClassified(new TimeoutException(), EgressFailure.Io, EgressStage.Upgrade,
            explicitStage: EgressStage.Upgrade);
        AssertClassified(new SocketException((int)SocketError.ConnectionReset), EgressFailure.Io,
            EgressStage.Io, explicitStage: EgressStage.Io);
        AssertClassified(new WsHandshakeException(302, "HTTP/1.1 302 Found"),
            EgressFailure.UpgradeRejected, EgressStage.Upgrade, explicitStage: EgressStage.Upgrade);
    }

    [Fact]
    public void Classify_ReturnsAnAlreadyTypedFailureUnchanged()
    {
        var typed = new EgressException(EgressFailure.TlsReset, EgressStage.Tls, new IOException("reset"));

        (EgressFailure failure, EgressStage stage) = EgressErrors.Classify(typed);

        Assert.Equal(EgressFailure.TlsReset, failure);
        Assert.Equal(EgressStage.Tls, stage);
        Assert.IsType<IOException>(typed.InnerException);
    }

    private static void AssertClassified(Exception exception, EgressFailure expectedFailure,
                                         EgressStage expectedStage, EgressStage? explicitStage = null)
    {
        if (explicitStage is not null)
        {
            (EgressFailure stagedFailure, EgressStage stagedStage) =
                EgressErrors.Classify(exception, explicitStage.Value);
            Assert.Equal(expectedFailure, stagedFailure);
            Assert.Equal(explicitStage.Value, stagedStage);
            return;
        }

        (EgressFailure failure, EgressStage stage) = EgressErrors.Classify(exception);
        Assert.Equal(expectedFailure, failure);
        Assert.Equal(expectedStage, stage);
    }
}
