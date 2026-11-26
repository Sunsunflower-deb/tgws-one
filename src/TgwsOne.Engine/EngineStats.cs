using TgwsOne.Engine.Desync;

namespace TgwsOne.Engine;

/// <summary>The counters the upstream engine printed once a minute, made thread-safe (one task per client).</summary>
public sealed class EngineStats
{
    private long _connectionsTotal, _connectionsActive, _connectionsWs, _connectionsH2, _h2TcpConnections,
        _h2Requests, _h2Errors, _connectionsTcpFallback, _connectionsCfproxy, _connectionsFronting,
        _connectionsBad, _connectionsMasked, _wsErrors, _bytesUp, _bytesDown, _poolHits, _poolMisses,
        _cfPoolHits, _cfPoolMisses, _egressAttempts, _egressOk, _egressConnectTimeout, _egressConnectRefused,
        _egressTlsTimeout, _egressTlsReset, _egressTlsAlert, _egressUpgradeRejected, _egressIo, _egressUnsupported;

    public long ConnectionsTotal => Interlocked.Read(ref _connectionsTotal);
    public long ConnectionsActive => Interlocked.Read(ref _connectionsActive);

    public void ConnectionOpened() { Interlocked.Increment(ref _connectionsTotal); Interlocked.Increment(ref _connectionsActive); }
    public void ConnectionClosed() => Interlocked.Decrement(ref _connectionsActive);
    public void WsConnection() => Interlocked.Increment(ref _connectionsWs);
    public void H2Connection() => Interlocked.Increment(ref _connectionsH2);
    public void H2TcpConnection() => Interlocked.Increment(ref _h2TcpConnections);
    public void H2Request() => Interlocked.Increment(ref _h2Requests);
    public void H2Error() => Interlocked.Increment(ref _h2Errors);
    public void TcpFallback() => Interlocked.Increment(ref _connectionsTcpFallback);
    public void CfProxy() => Interlocked.Increment(ref _connectionsCfproxy);
    public void Fronting() => Interlocked.Increment(ref _connectionsFronting);
    public void BadHandshake() => Interlocked.Increment(ref _connectionsBad);
    public void Masked() => Interlocked.Increment(ref _connectionsMasked);
    public void WsError() => Interlocked.Increment(ref _wsErrors);
    public void Up(long bytes) => Interlocked.Add(ref _bytesUp, bytes);
    public void Down(long bytes) => Interlocked.Add(ref _bytesDown, bytes);
    public void PoolHit() => Interlocked.Increment(ref _poolHits);
    public void PoolMiss() => Interlocked.Increment(ref _poolMisses);
    public void CfPoolHit() => Interlocked.Increment(ref _cfPoolHits);
    public void CfPoolMiss() => Interlocked.Increment(ref _cfPoolMisses);

    public void EgressAttempt() => Interlocked.Increment(ref _egressAttempts);
    public void EgressSuccess() => Interlocked.Increment(ref _egressOk);

    public void EgressFailed(EgressFailure failure)
    {
        switch (failure)
        {
            case EgressFailure.ConnectTimeout: Interlocked.Increment(ref _egressConnectTimeout); break;
            case EgressFailure.ConnectRefused: Interlocked.Increment(ref _egressConnectRefused); break;
            case EgressFailure.TlsTimeout: Interlocked.Increment(ref _egressTlsTimeout); break;
            case EgressFailure.TlsReset: Interlocked.Increment(ref _egressTlsReset); break;
            case EgressFailure.TlsAlert: Interlocked.Increment(ref _egressTlsAlert); break;
            case EgressFailure.UpgradeRejected: Interlocked.Increment(ref _egressUpgradeRejected); break;
            case EgressFailure.Io: Interlocked.Increment(ref _egressIo); break;
            case EgressFailure.Unsupported: Interlocked.Increment(ref _egressUnsupported); break;
            // None is not a failure; counting it separately would double the success counter.
        }
    }

    public long EgressAttempts => Interlocked.Read(ref _egressAttempts);
    public long EgressOk => Interlocked.Read(ref _egressOk);
    public long EgressConnectTimeout => Interlocked.Read(ref _egressConnectTimeout);
    public long EgressConnectRefused => Interlocked.Read(ref _egressConnectRefused);
    public long EgressTlsTimeout => Interlocked.Read(ref _egressTlsTimeout);
    public long EgressTlsReset => Interlocked.Read(ref _egressTlsReset);
    public long EgressTlsAlert => Interlocked.Read(ref _egressTlsAlert);
    public long EgressUpgradeRejected => Interlocked.Read(ref _egressUpgradeRejected);
    public long EgressIo => Interlocked.Read(ref _egressIo);
    public long EgressUnsupported => Interlocked.Read(ref _egressUnsupported);

    public string Summary()
    {
        long poolTotal = PoolHits + PoolMisses;
        string pool = poolTotal == 0 ? "n/a" : $"{PoolHits}/{poolTotal}";
        long cfPoolTotal = CfPoolHits + CfPoolMisses;
        string cfPool = cfPoolTotal == 0 ? "n/a" : $"{CfPoolHits}/{cfPoolTotal}";

        return $"total={ConnectionsTotal} active={ConnectionsActive} ws={Interlocked.Read(ref _connectionsWs)} " +
               $"h2={Interlocked.Read(ref _connectionsH2)} h2_tcp={Interlocked.Read(ref _h2TcpConnections)} " +
               $"h2_req={Interlocked.Read(ref _h2Requests)} h2_err={Interlocked.Read(ref _h2Errors)} " +
               $"tcp_fb={Interlocked.Read(ref _connectionsTcpFallback)} cf={Interlocked.Read(ref _connectionsCfproxy)} " +
               $"front={Interlocked.Read(ref _connectionsFronting)} bad={Interlocked.Read(ref _connectionsBad)} " +
               $"masked={Interlocked.Read(ref _connectionsMasked)} err={Interlocked.Read(ref _wsErrors)} " +
               $"pool={pool} cf_pool={cfPool} egress={Interlocked.Read(ref _egressOk)}/" +
               $"{Interlocked.Read(ref _egressAttempts)} up={HumanBytes(Interlocked.Read(ref _bytesUp))} " +
               $"down={HumanBytes(Interlocked.Read(ref _bytesDown))}";
    }

    public long PoolHits => Interlocked.Read(ref _poolHits);
    public long PoolMisses => Interlocked.Read(ref _poolMisses);
    public long CfPoolHits => Interlocked.Read(ref _cfPoolHits);
    public long CfPoolMisses => Interlocked.Read(ref _cfPoolMisses);

    internal static string HumanBytes(long n)
    {
        double value = n;
        foreach (string unit in new[] { "B", "KB", "MB", "GB" })
        {
            if (Math.Abs(value) < 1024) return value.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture) + unit;
            value /= 1024;
        }
        return value.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture) + "TB";
    }
}
