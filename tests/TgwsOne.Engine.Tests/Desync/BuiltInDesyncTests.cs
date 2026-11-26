using TgwsOne.Engine.Desync;

namespace TgwsOne.Engine.Tests.Desync;

public sealed class BuiltInDesyncTests
{
    [Fact]
    public void IdsAndOrder_FollowTheTable()
    {
        Assert.Equal(
        [
            "fake", "split-sni", "split-midsni", "fake-split",
            "disorder-1", "disorder-sld", "oob-sni", "tlsrec-midsni",
        ], BuiltInDesync.Ids);
    }

    [Fact]
    public void EveryStrategy_HasARussianTitle()
    {
        Assert.All(BuiltInDesync.BuiltIn, strategy =>
        {
            Assert.False(string.IsNullOrWhiteSpace(strategy.Title));
            Assert.Contains(strategy.Title!, character => character is >= 'А' and <= 'я');
        });
    }

    [Fact]
    public void EveryStrategy_ProducesAPlanOnATypicalClientHello()
    {
        byte[] hello = DesyncTestData.ClientHello(tls13: true);
        ClientHelloLayout layout = DesyncTestData.Parse(hello);

        foreach (DesyncStrategy strategy in BuiltInDesync.BuiltIn)
        {
            DesyncPlan plan = DesyncPlanner.Plan(strategy, layout, hello, DesyncCapabilities.All);
            Assert.False(plan.IsEmpty, $"{strategy.Id} produced no segments");
        }
    }

    [Fact]
    public void WithoutPlatformFlags_StrategiesDegradeToSplitsOrAnUnsupportedEmptyPlan()
    {
        byte[] hello = DesyncTestData.ClientHello(tls13: true);
        ClientHelloLayout layout = DesyncTestData.Parse(hello);

        foreach (DesyncStrategy strategy in BuiltInDesync.BuiltIn)
        {
            DesyncPlan plan = DesyncPlanner.Plan(strategy, layout, hello, DesyncCapabilities.None);
            if (plan.IsEmpty)
            {
                Assert.Contains(plan.Skipped,
                    skipped => skipped.Reason == DesyncSkipReason.Unsupported);
            }
            else
            {
                Assert.DoesNotContain(plan.Segments, segment =>
                    segment.Mode is DesyncSegmentMode.LowTtl or DesyncSegmentMode.FakeSplice
                        or DesyncSegmentMode.Urgent);
            }
        }
    }

    [Fact]
    public void Steps_MatchTheTable()
    {
        Assert.IsType<Fake>(Assert.Single(BuiltInDesync.Find("fake")!.Steps));
        Assert.Equal(new Split(new SniEnd(1)), Assert.Single(BuiltInDesync.Find("split-sni")!.Steps));
        Assert.Equal(
        [
            new Split(new SniMid()),
            new Split(new SniEnd(1)),
        ], BuiltInDesync.Find("split-midsni")!.Steps);
        Assert.Equal(2, BuiltInDesync.Find("fake-split")!.Steps.Count);
        Assert.Equal(new Disorder(new Absolute(1)), Assert.Single(BuiltInDesync.Find("disorder-1")!.Steps));
        Assert.Equal(
        [
            new Disorder(new SldMid()),
            new Disorder(new SniEnd(1)),
        ], BuiltInDesync.Find("disorder-sld")!.Steps);
        Assert.Equal(new Oob(new SniMid()), Assert.Single(BuiltInDesync.Find("oob-sni")!.Steps));
        Assert.Equal(new TlsRec(new SniMid()), Assert.Single(BuiltInDesync.Find("tlsrec-midsni")!.Steps));
    }

    [Fact]
    public void Find_UnknownOrNull_IsNull()
    {
        Assert.Null(BuiltInDesync.Find("nonsense"));
        Assert.Null(BuiltInDesync.Find(null));
        Assert.False(BuiltInDesync.IsBuiltIn("auto"));
    }
}
