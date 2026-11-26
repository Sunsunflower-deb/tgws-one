namespace TgwsOne.Engine.Desync;

/// <summary>
/// The buffer the planner actually sliced. When a strategy contains <see cref="TlsRec"/> the planner rebuilds the
/// first flight with extra record headers, so a fake segment's offset refers to the rebuilt buffer, not to the bytes
/// the caller wrote. Re-running only the record cuts recovers that buffer without duplicating the planner's logic.
/// </summary>
internal static class PlannedFirstFlight
{
    public static ReadOnlyMemory<byte> Recover(DesyncStrategy strategy, ClientHelloLayout layout,
                                               ReadOnlyMemory<byte> firstFlight, DesyncCapabilities capabilities)
    {
        int recordCuts = 0;
        foreach (DesyncStep step in strategy.Steps)
            if (step is TlsRec) recordCuts++;
        if (recordCuts == 0) return firstFlight;

        var recordsOnly = new DesyncStep[recordCuts];
        int index = 0;
        foreach (DesyncStep step in strategy.Steps)
            if (step is TlsRec) recordsOnly[index++] = step;

        DesyncPlan plan = DesyncPlanner.Plan(
            new DesyncStrategy(strategy.Id, recordsOnly), layout, firstFlight.Span, capabilities);
        if (plan.IsEmpty) return firstFlight;

        int length = 0;
        foreach (DesyncSegment segment in plan.Segments) length += segment.Length;
        var rebuilt = new byte[length];
        int offset = 0;
        foreach (DesyncSegment segment in plan.Segments)
        {
            segment.Bytes.Span.CopyTo(rebuilt.AsSpan(offset));
            offset += segment.Length;
        }
        return rebuilt;
    }
}
