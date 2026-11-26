using System.Buffers.Binary;
using TgwsOne.Engine.Desync;

namespace TgwsOne.Engine.Tests.Desync;

public sealed class DesyncPlannerTests
{
    private static byte[] PlanBytes(DesyncPlan plan) =>
        [.. plan.Segments.SelectMany(segment => segment.Bytes.Span.ToArray())];

    [Fact]
    public void SplitAtAbsoluteOffsetProducesTwoNormalSegments()
    {
        byte[] hello = DesyncTestData.ClientHello();
        ClientHelloLayout layout = DesyncTestData.Parse(hello);

        DesyncPlan plan = DesyncPlanner.Plan(
            new DesyncStrategy("split", [new Split(new Absolute(10))]),
            layout, hello, DesyncCapabilities.All);

        Assert.Empty(plan.Skipped);
        Assert.Equal(2, plan.Segments.Count);
        Assert.All(plan.Segments, segment => Assert.Equal(DesyncSegmentMode.Normal, segment.Mode));
        Assert.Equal(0, plan.Segments[0].Offset);
        Assert.Equal(10, plan.Segments[0].Length);
        Assert.Equal(10, plan.Segments[1].Offset);
        Assert.Equal(hello.Length - 10, plan.Segments[1].Length);
        Assert.Equal(hello, PlanBytes(plan));
    }

    [Fact]
    public void SniBoundSplitsAreResolvedAndStayAscending()
    {
        byte[] hello = DesyncTestData.ClientHello();
        ClientHelloLayout layout = DesyncTestData.Parse(hello);
        int sniStart = layout.Resolve(new SniStart())!.Value;
        int sniMid = layout.Resolve(new SniMid())!.Value;

        DesyncPlan plan = DesyncPlanner.Plan(
            new DesyncStrategy("sni", [new Split(new SniStart()), new Split(new SniMid())]),
            layout, hello, DesyncCapabilities.All);

        Assert.Empty(plan.Skipped);
        Assert.Equal(3, plan.Segments.Count);
        Assert.Equal([0, sniStart, sniMid], plan.Segments.Select(segment => segment.Offset));
        Assert.Equal(hello, PlanBytes(plan));
    }

    [Fact]
    public void TlsRecRebuildsBytesAndRemapsLaterSniPositions()
    {
        byte[] hello = DesyncTestData.ClientHello();
        ClientHelloLayout layout = DesyncTestData.Parse(hello);
        int recordCut = 20;
        int sniStart = layout.Resolve(new SniStart())!.Value;
        Assert.True(sniStart > recordCut);

        DesyncPlan plan = DesyncPlanner.Plan(
            new DesyncStrategy("tlsrec", [new TlsRec(new Absolute(recordCut)), new Split(new SniStart())]),
            layout, hello, DesyncCapabilities.All);

        Assert.Empty(plan.Skipped);
        byte[] rebuilt = DesyncTestData.SplitRecord(hello, recordCut - 5);
        Assert.Equal(rebuilt, PlanBytes(plan));

        // The five-byte header inserted at the cut moves every later SNI offset, so the split must land
        // five bytes after the original SNI start, not at it.
        Assert.Equal(2, plan.Segments.Count);
        Assert.Equal(0, plan.Segments[0].Offset);
        Assert.Equal(sniStart + 5, plan.Segments[0].Length);
        Assert.Equal(sniStart + 5, plan.Segments[1].Offset);
        Assert.Equal(0x16, rebuilt[recordCut]);
        int firstRecordLength = BinaryPrimitives.ReadUInt16BigEndian(rebuilt.AsSpan(3, 2));
        Assert.Equal(recordCut - 5, firstRecordLength);
    }

    [Theory]
    [InlineData(10)]
    [InlineData(55)]
    [InlineData(0)]
    public void FakeCoversTheReplacedRange(int cut)
    {
        byte[] hello = DesyncTestData.ClientHello();
        ClientHelloLayout layout = DesyncTestData.Parse(hello);

        DesyncPlan plan = DesyncPlanner.Plan(
            new DesyncStrategy("fake", [new Fake(new Absolute(cut), Ttl: 3, Sni: "fake.example")]),
            layout, hello, DesyncCapabilities.All);

        if (cut == 0)
        {
            Assert.Empty(plan.Segments);
            Assert.Equal(DesyncSkipReason.Empty, Assert.Single(plan.Skipped).Reason);
            return;
        }

        Assert.Empty(plan.Skipped);
        DesyncSegment fake = plan.Segments[0];
        Assert.Equal(DesyncSegmentMode.FakeSplice, fake.Mode);
        Assert.True(fake.NeedsFakeData);
        Assert.Equal(cut, fake.Length);
        Assert.Equal(0, fake.Offset);
        Assert.Equal(3, fake.FakeTtl);
        Assert.Equal("fake.example", fake.FakeSni);
        Assert.Equal(hello.Length - cut, plan.Segments[1].Length);
    }

    [Fact]
    public void FakeUsesLayoutSniWhenStepHasNone()
    {
        byte[] hello = DesyncTestData.ClientHello();
        ClientHelloLayout layout = DesyncTestData.Parse(hello);

        DesyncPlan plan = DesyncPlanner.Plan(
            new DesyncStrategy("fake", [new Fake(new Absolute(10), Ttl: null, Sni: null)]),
            layout, hello, DesyncCapabilities.All);

        DesyncSegment fake = Assert.Single(
            plan.Segments, segment => segment.Mode == DesyncSegmentMode.FakeSplice);
        Assert.Equal(DesyncTestData.Host, fake.FakeSni);
    }

    [Fact]
    public void DisorderAndOobUseTheirRequiredModes()
    {
        byte[] hello = DesyncTestData.ClientHello();
        ClientHelloLayout layout = DesyncTestData.Parse(hello);

        DesyncPlan plan = DesyncPlanner.Plan(
            new DesyncStrategy("modes", [new Disorder(new Absolute(20)), new Oob(new Absolute(30))]),
            layout, hello, DesyncCapabilities.All);

        Assert.Empty(plan.Skipped);
        Assert.Equal(DesyncSegmentMode.LowTtl, plan.Segments[0].Mode);
        Assert.Equal(20, plan.Segments[0].Length);
        Assert.Equal(DesyncSegmentMode.Normal, plan.Segments[1].Mode);
        Assert.Equal(10, plan.Segments[1].Length);
        Assert.Equal(DesyncSegmentMode.Urgent, plan.Segments[2].Mode);
        Assert.Equal(30, plan.Segments[2].Offset);
        Assert.Equal(1, plan.Segments[2].Length);
        Assert.Equal(hello, PlanBytes(plan));
    }

    [Fact]
    public void PlatformWithoutFlagsKeepsSplitsButSkipsOthers()
    {
        byte[] hello = DesyncTestData.ClientHello();
        ClientHelloLayout layout = DesyncTestData.Parse(hello);

        DesyncPlan plan = DesyncPlanner.Plan(
            new DesyncStrategy("degraded",
            [
                new Disorder(new Absolute(10)),
                new Fake(new Absolute(20), Ttl: 1, Sni: null),
                new Oob(new Absolute(30)),
                new Split(new Absolute(40)),
            ]),
            layout, hello, DesyncCapabilities.None);

        Assert.Equal(3, plan.Skipped.Count);
        Assert.All(plan.Skipped, skipped => Assert.Equal(DesyncSkipReason.Unsupported, skipped.Reason));
        Assert.Equal(2, plan.Segments.Count);
        Assert.All(plan.Segments, segment => Assert.Equal(DesyncSegmentMode.Normal, segment.Mode));
        Assert.Equal(hello, PlanBytes(plan));
    }

    [Theory]
    [InlineData(DesyncCapabilities.LowTtl, typeof(Disorder))]
    [InlineData(DesyncCapabilities.SpliceFake, typeof(Fake))]
    [InlineData(DesyncCapabilities.Urgent, typeof(Oob))]
    public void EachCapabilityEnablesExactlyItsStep(DesyncCapabilities capability, Type enabled)
    {
        byte[] hello = DesyncTestData.ClientHello();
        ClientHelloLayout layout = DesyncTestData.Parse(hello);
        DesyncStep[] steps =
        [
            new Disorder(new Absolute(10)),
            new Fake(new Absolute(20), Ttl: 1, Sni: null),
            new Oob(new Absolute(30)),
        ];

        DesyncPlan plan = DesyncPlanner.Plan(new DesyncStrategy("one", steps), layout, hello, capability);

        Assert.Equal(2, plan.Skipped.Count);
        Assert.All(plan.Skipped, skipped => Assert.Equal(DesyncSkipReason.Unsupported, skipped.Reason));
        Assert.Single(plan.Segments, segment => segment.Mode switch
        {
            DesyncSegmentMode.LowTtl => enabled == typeof(Disorder),
            DesyncSegmentMode.FakeSplice => enabled == typeof(Fake),
            DesyncSegmentMode.Urgent => enabled == typeof(Oob),
            _ => false,
        });
    }

    [Fact]
    public void EmptyStrategyAndOnlyInapplicableStepsGiveEmptyPlan()
    {
        byte[] hello = DesyncTestData.ClientHello(host: null);
        ClientHelloLayout layout = DesyncTestData.Parse(hello);

        DesyncPlan empty = DesyncPlanner.Plan(
            new DesyncStrategy("empty", []), layout, hello, DesyncCapabilities.All);
        Assert.True(empty.IsEmpty);
        Assert.Empty(empty.Skipped);

        DesyncPlan noSni = DesyncPlanner.Plan(
            new DesyncStrategy("no-sni",
            [
                new Split(new SniStart()),
                new Disorder(new SniMid()),
                new Oob(new SldMid()),
            ]),
            layout, hello, DesyncCapabilities.All);

        Assert.True(noSni.IsEmpty);
        Assert.Equal(3, noSni.Skipped.Count);
        Assert.All(noSni.Skipped, skipped => Assert.Equal(DesyncSkipReason.SniUnavailable, skipped.Reason));
    }

    [Fact]
    public void PauseBecomesADelayedEmptySegment()
    {
        byte[] hello = DesyncTestData.ClientHello();
        ClientHelloLayout layout = DesyncTestData.Parse(hello);
        TimeSpan delay = TimeSpan.FromMilliseconds(7);

        DesyncPlan plan = DesyncPlanner.Plan(
            new DesyncStrategy("pause", [new Pause(delay)]),
            layout, hello, DesyncCapabilities.None);

        Assert.Empty(plan.Skipped);
        Assert.Equal(2, plan.Segments.Count);
        Assert.Equal(0, plan.Segments[0].Length);
        Assert.Equal(delay, plan.Segments[0].Delay);
        Assert.Equal(hello, PlanBytes(plan));
    }

    [Fact]
    public void NonAscendingSplitIsSkipped()
    {
        byte[] hello = DesyncTestData.ClientHello();
        ClientHelloLayout layout = DesyncTestData.Parse(hello);

        DesyncPlan plan = DesyncPlanner.Plan(
            new DesyncStrategy("backwards", [new Split(new Absolute(20)), new Split(new Absolute(10))]),
            layout, hello, DesyncCapabilities.All);

        DesyncSkippedStep skipped = Assert.Single(plan.Skipped);
        Assert.IsType<Split>(skipped.Step);
        Assert.Equal(DesyncSkipReason.PositionNotAscending, skipped.Reason);
        Assert.Equal(hello, PlanBytes(plan));
    }

    [Fact]
    public void OutOfBoundsPositionsAreSkipped()
    {
        byte[] hello = DesyncTestData.ClientHello();
        ClientHelloLayout layout = DesyncTestData.Parse(hello);

        DesyncPlan plan = DesyncPlanner.Plan(
            new DesyncStrategy("bounds",
            [
                new Split(new Absolute(hello.Length + 1)),
                new Disorder(new FromEnd(-1)),
                new Oob(new Absolute(hello.Length)),
            ]),
            layout, hello, DesyncCapabilities.All);

        Assert.True(plan.IsEmpty);
        Assert.Equal(3, plan.Skipped.Count);
        Assert.All(plan.Skipped, skipped => Assert.Equal(DesyncSkipReason.PositionOutOfBounds, skipped.Reason));
    }
}
