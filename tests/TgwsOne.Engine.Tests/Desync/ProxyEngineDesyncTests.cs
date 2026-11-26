using TgwsOne.Engine.Desync;
using TgwsOne.Engine.Relay;

namespace TgwsOne.Engine.Tests.Desync;

public sealed class ProxyEngineDesyncTests
{
    private const string Secret = "0123456789abcdef0123456789abcdef";

    private static EngineConfig Config() => new()
    {
        Secret = Secret,
        PoolSize = 0,
        FallbackCfProxy = false,
        CfproxyH2Media = false,
    };

    [Fact]
    public async Task ChangingTheController_ClosesOnlyIdleConnections()
    {
        var ws = new StandInPool(idle: 2, active: 1);
        var worker = new StandInPool(idle: 1, active: 1);
        var h2 = new StandInPool(idle: 1, active: 1);
        await using var engine = new ProxyEngine(Config(), log: null, desync: null, idlePools: [ws, worker, h2]);
        var controller = new DesyncController([new DesyncStrategy("split", [new Split(new SniMid())])]);

        engine.Desync = controller;

        Assert.Same(controller, engine.Desync);
        Assert.Equal(1, ws.CloseCalls);
        Assert.Equal(1, worker.CloseCalls);
        Assert.Equal(1, h2.CloseCalls);
        Assert.Equal(0, ws.Idle);
        Assert.Equal(1, ws.Active);
        Assert.Equal(1, worker.Active);
        Assert.Equal(1, h2.Active);
    }

    [Fact]
    public async Task SettingTheSameControllerAgain_IsNotAChange()
    {
        var ws = new StandInPool(idle: 1, active: 0);
        await using var engine = new ProxyEngine(Config(), log: null, desync: null, idlePools: [ws]);
        var controller = new DesyncController();

        engine.Desync = controller;
        engine.Desync = controller;

        Assert.Equal(1, ws.CloseCalls);
    }

    [Fact]
    public async Task SelectingAStrategyInPlace_AlsoDropsIdleConnections()
    {
        var ws = new StandInPool(idle: 1, active: 1);
        var controller = new DesyncController();
        await using var engine = new ProxyEngine(Config(), log: null, desync: controller, idlePools: [ws]);
        Assert.Equal(0, ws.CloseCalls);

        controller.Select(new DesyncStrategy("split", [new Split(new SniMid())]));

        Assert.Equal(1, ws.CloseCalls);
        Assert.Equal(0, ws.Idle);
        Assert.Equal(1, ws.Active);
    }

    [Fact]
    public async Task NullController_DisablesDesync_AndDropsIdleConnectionsOnce()
    {
        var ws = new StandInPool(idle: 1, active: 1);
        var controller = new DesyncController();
        await using var engine = new ProxyEngine(Config(), log: null, desync: controller, idlePools: [ws]);

        engine.Desync = null;

        Assert.Null(engine.Desync);
        Assert.Equal(1, ws.CloseCalls);
        Assert.Equal(0, ws.Idle);
        Assert.Equal(1, ws.Active);

        // Already null: a second assignment changes nothing and touches no pool.
        engine.Desync = null;
        Assert.Equal(1, ws.CloseCalls);
    }

    [Fact]
    public async Task AnEngineWithoutAController_StaysPlain()
    {
        var ws = new StandInPool(idle: 1, active: 0);
        await using var engine = new ProxyEngine(Config(), log: null, desync: null, idlePools: [ws]);

        Assert.Null(engine.Desync);
        Assert.Equal(0, ws.CloseCalls);
    }

    /// <summary>A stand-in pool: idle entries disappear on close, active sessions are never the pool's to touch.</summary>
    private sealed class StandInPool(int idle, int active) : IIdleConnectionPool
    {
        public int Idle { get; private set; } = idle;
        public int Active { get; } = active;
        public int CloseCalls { get; private set; }

        public void CloseIdle()
        {
            CloseCalls++;
            Idle = 0;
        }
    }
}
