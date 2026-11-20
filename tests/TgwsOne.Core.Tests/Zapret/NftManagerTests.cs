using TgwsOne.Core.Zapret;

namespace TgwsOne.Core.Tests.Zapret;

public class NftManagerTests
{
    private const string Key = "net.netfilter.nf_conntrack_tcp_be_liberal";

    private const string Golden = """
        table inet tgwsone {
          chain post {
            type filter hook postrouting priority 101; policy accept;
            meta mark & 0x40000000 == 0 tcp dport 443 ct original packets 1-12 queue num 3443 bypass
          }
          chain pre {
            type filter hook prerouting priority -101; policy accept;
            meta mark & 0x40000000 == 0 tcp sport 443 ct reply packets 1-12 queue num 3443 bypass
          }
          chain predefrag {
            type filter hook output priority -401; policy accept;
            meta mark & 0x40000000 != 0 notrack
          }
        }

        """;

    [Fact]
    public void Ruleset_Golden() => Assert.Equal(Golden.Replace("\r\n", "\n"), NftManager.Ruleset(3443));

    [Fact]
    public void Ruleset_UsesTheGivenQueueNumber()
    {
        string rules = NftManager.Ruleset(77);

        Assert.Equal(2, rules.Split("queue num 77 bypass").Length - 1);
        Assert.DoesNotContain("3443", rules);
    }

    [Fact]
    public async Task Apply_SendsTheExactRulesetToNft()
    {
        var runner = new FakeRunner();
        var nft = new NftManager(runner, new FakeSysctl());

        await nft.ApplyAsync();

        RunnerCall call = Assert.Single(runner.Calls);
        Assert.Equal("nft", call.File);
        Assert.Equal(["-f", "-"], call.Args);
        // "table" creates the table when it doesn't exist yet, so the following "delete" can't fail on a first run:
        // the whole replacement is one atomic transaction.
        Assert.Equal("table inet tgwsone\ndelete table inet tgwsone\n" + Golden.Replace("\r\n", "\n"), call.Stdin);
    }

    [Fact]
    public async Task Apply_SetsLiberalTo1_AndRemoveRestoresTheOldValue()
    {
        var sysctl = new FakeSysctl { Values = { [Key] = "0" } };
        var nft = new NftManager(new FakeRunner(), sysctl);

        await nft.ApplyAsync();
        Assert.Equal("1", sysctl.Values[Key]);

        await nft.RemoveAsync();
        Assert.Equal("0", sysctl.Values[Key]);
    }

    [Fact]
    public async Task Apply_Twice_StillRestoresTheOriginalValue()
    {
        var sysctl = new FakeSysctl { Values = { [Key] = "0" } };
        var nft = new NftManager(new FakeRunner(), sysctl);

        await nft.ApplyAsync();
        await nft.ApplyAsync();   // a strategy change re-applies; the "old value" is still the one before us
        await nft.RemoveAsync();

        Assert.Equal("0", sysctl.Values[Key]);
    }

    [Fact]
    public async Task Apply_WhenAlready1_LeavesItAlone()
    {
        var sysctl = new FakeSysctl { Values = { [Key] = "1" } };
        var nft = new NftManager(new FakeRunner(), sysctl);

        await nft.ApplyAsync();
        await nft.RemoveAsync();

        Assert.Equal("1", sysctl.Values[Key]);
    }

    [Fact]
    public async Task Remove_RunsTheDelete()
    {
        var runner = new FakeRunner();
        var nft = new NftManager(runner, new FakeSysctl());

        await nft.RemoveAsync();

        RunnerCall call = Assert.Single(runner.Calls);
        Assert.Equal("nft", call.File);
        Assert.Equal(["delete", "table", "inet", "tgwsone"], call.Args);
        Assert.Null(call.Stdin);
    }

    [Fact]
    public async Task Remove_TableAbsent_DoesNotThrow_AndStillRestoresTheSysctl()
    {
        var sysctl = new FakeSysctl { Values = { [Key] = "0" } };
        var runner = new FakeRunner();
        var nft = new NftManager(runner, sysctl);
        await nft.ApplyAsync();
        runner.Respond = _ => (1, "Error: No such file or directory\ndelete table inet tgwsone\n^^^^^^^^^^^^^^^^^^^^^^^^^^");

        await nft.RemoveAsync();

        Assert.Equal("0", sysctl.Values[Key]);
    }

    [Fact]
    public async Task Remove_OtherFailure_Throws_ButTheSysctlIsRestoredFirst()
    {
        var sysctl = new FakeSysctl { Values = { [Key] = "0" } };
        var runner = new FakeRunner();
        var nft = new NftManager(runner, sysctl);
        await nft.ApplyAsync();
        runner.Respond = _ => (1, "Error: Operation not permitted");

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => nft.RemoveAsync());

        Assert.Contains("Operation not permitted", ex.Message);
        Assert.Equal("0", sysctl.Values[Key]);
    }

    [Fact]
    public async Task Apply_NftFails_ThrowsWithItsOutput_AndTouchesNoSysctl()
    {
        var sysctl = new FakeSysctl { Values = { [Key] = "0" } };
        var runner = new FakeRunner { Respond = _ => (1, "Error: Could not process rule: Operation not permitted") };
        var nft = new NftManager(runner, sysctl);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => nft.ApplyAsync());

        Assert.Contains("Could not process rule: Operation not permitted", ex.Message);
        Assert.Empty(sysctl.Writes);

        // A later Remove has nothing of ours to restore.
        runner.Respond = _ => (0, "");
        await nft.RemoveAsync();
        Assert.Empty(sysctl.Writes);
    }

    [Fact]
    public async Task Remove_WithoutApply_DoesNotWriteTheSysctl()
    {
        var sysctl = new FakeSysctl { Values = { [Key] = "0" } };

        await new NftManager(new FakeRunner(), sysctl).RemoveAsync();

        Assert.Empty(sysctl.Writes);
    }

    [Fact]
    public async Task MissingSysctl_IsNotAnError()
    {
        var sysctl = new FakeSysctl();   // conntrack not loaded: no such key
        var nft = new NftManager(new FakeRunner(), sysctl);

        await nft.ApplyAsync();
        await nft.RemoveAsync();

        Assert.Empty(sysctl.Writes);
    }

    [Fact]
    public async Task SysctlWriteFailure_DoesNotFailApply()
    {
        var sysctl = new FakeSysctl { Values = { [Key] = "0" }, FailWrites = true };
        var nft = new NftManager(new FakeRunner(), sysctl);

        await nft.ApplyAsync();
        await nft.RemoveAsync();

        Assert.Equal("0", sysctl.Values[Key]);
    }

    [Fact]
    public async Task Cancellation_IsPassedToTheRunner()
    {
        using var cts = new CancellationTokenSource();
        CancellationToken seen = default;
        var runner = new FakeRunner();
        var counting = new TokenCapturingRunner(runner, t => seen = t);
        var nft = new NftManager(counting, new FakeSysctl());

        await nft.ApplyAsync(cts.Token);

        Assert.Equal(cts.Token, seen);
    }

    private sealed class TokenCapturingRunner(ICommandRunner inner, Action<CancellationToken> capture) : ICommandRunner
    {
        public Task<(int Code, string Output)> RunAsync(string file, IReadOnlyList<string> args, string? stdin = null, CancellationToken ct = default)
        {
            capture(ct);
            return inner.RunAsync(file, args, stdin, ct);
        }
    }
}
