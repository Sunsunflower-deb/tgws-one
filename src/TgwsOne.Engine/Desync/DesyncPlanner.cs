using System.Buffers.Binary;

namespace TgwsOne.Engine.Desync;

/// <summary>
/// Turns a strategy into the exact segments a sender has to write. Record rebuilding happens first, then
/// the remaining steps are applied in strategy order; a step that would cut before the previous cut is
/// reported instead of silently reordering the plan.
/// </summary>
public static class DesyncPlanner
{
    public static DesyncPlan Plan(DesyncStrategy? strategy, ClientHelloLayout layout,
                                  ReadOnlySpan<byte> firstFlight, DesyncCapabilities capabilities)
    {
        ArgumentNullException.ThrowIfNull(layout);
        var segments = new List<DesyncSegment>();
        var skipped = new List<DesyncSkippedStep>();
        if (strategy is null || strategy.Steps.Count == 0)
            return new DesyncPlan(segments, skipped);

        byte[] bytes = firstFlight.ToArray();
        ClientHelloLayout current = layout;
        bool accepted = false;

        // TlsRec changes the byte layout, so every later position has to be resolved against the rebuilt
        // buffer. That is why all record cuts are applied before any other step is looked at.
        int lastRecordCut = -1;
        foreach (DesyncStep step in strategy.Steps)
        {
            if (step is not TlsRec recordCut) continue;
            if (!TryResolve(current, recordCut.Pos, out int position, out DesyncSkipReason reason))
            {
                skipped.Add(new DesyncSkippedStep(step, reason));
                continue;
            }
            if (position <= lastRecordCut)
            {
                skipped.Add(new DesyncSkippedStep(step, DesyncSkipReason.PositionNotAscending));
                continue;
            }
            if (!TryRebuildRecord(bytes, current, position, out byte[] rebuilt, out ClientHelloLayout rebuiltLayout))
            {
                skipped.Add(new DesyncSkippedStep(step, DesyncSkipReason.PositionOutOfBounds));
                continue;
            }
            bytes = rebuilt;
            current = rebuiltLayout;
            lastRecordCut = position;
            accepted = true;
        }

        int cursor = 0;
        foreach (DesyncStep step in strategy.Steps)
        {
            if (step is TlsRec) continue;
            DesyncSkipReason reason;
            switch (step)
            {
                case Pause pause:
                    if (pause.Delay < TimeSpan.Zero)
                    {
                        skipped.Add(new DesyncSkippedStep(step, DesyncSkipReason.Empty));
                        break;
                    }
                    segments.Add(new DesyncSegment(ReadOnlyMemory<byte>.Empty, DesyncSegmentMode.Normal, pause.Delay, cursor));
                    accepted = true;
                    break;

                case Split split:
                    if (!TryResolve(current, split.Pos, out int splitAt, out reason))
                    {
                        skipped.Add(new DesyncSkippedStep(step, reason));
                        break;
                    }
                    if (splitAt < cursor)
                    {
                        skipped.Add(new DesyncSkippedStep(step, DesyncSkipReason.PositionNotAscending));
                        break;
                    }
                    AddNormal(segments, bytes, cursor, splitAt);
                    cursor = splitAt;
                    accepted = true;
                    break;

                case Disorder disorder:
                    if ((capabilities & DesyncCapabilities.LowTtl) == 0)
                    {
                        skipped.Add(new DesyncSkippedStep(step, DesyncSkipReason.Unsupported));
                        break;
                    }
                    if (!TryResolve(current, disorder.Pos, out int disorderAt, out reason))
                    {
                        skipped.Add(new DesyncSkippedStep(step, reason));
                        break;
                    }
                    if (disorderAt < cursor)
                    {
                        skipped.Add(new DesyncSkippedStep(step, DesyncSkipReason.PositionNotAscending));
                        break;
                    }
                    AddSegment(segments, bytes, cursor, disorderAt, DesyncSegmentMode.LowTtl);
                    cursor = disorderAt;
                    accepted = true;
                    break;

                case Fake fake:
                    if ((capabilities & DesyncCapabilities.SpliceFake) == 0)
                    {
                        skipped.Add(new DesyncSkippedStep(step, DesyncSkipReason.Unsupported));
                        break;
                    }
                    if (!TryResolve(current, fake.Pos, out int fakeAt, out reason))
                    {
                        skipped.Add(new DesyncSkippedStep(step, reason));
                        break;
                    }
                    if (fakeAt < cursor)
                    {
                        skipped.Add(new DesyncSkippedStep(step, DesyncSkipReason.PositionNotAscending));
                        break;
                    }
                    if (fakeAt == cursor)
                    {
                        skipped.Add(new DesyncSkippedStep(step, DesyncSkipReason.Empty));
                        break;
                    }
                    // The sender fills this range with a fake ClientHello of the same length; the real bytes
                    // stay in the stream and are recovered by retransmission after the buffer is overwritten.
                    segments.Add(new DesyncSegment(
                        new byte[fakeAt - cursor],
                        DesyncSegmentMode.FakeSplice,
                        TimeSpan.Zero,
                        cursor,
                        NeedsFakeData: true,
                        FakeSni: fake.Sni ?? current.Sni,
                        FakeTtl: fake.Ttl));
                    cursor = fakeAt;
                    accepted = true;
                    break;

                case Oob oob:
                    if ((capabilities & DesyncCapabilities.Urgent) == 0)
                    {
                        skipped.Add(new DesyncSkippedStep(step, DesyncSkipReason.Unsupported));
                        break;
                    }
                    if (!TryResolve(current, oob.Pos, out int oobAt, out reason))
                    {
                        skipped.Add(new DesyncSkippedStep(step, reason));
                        break;
                    }
                    if (oobAt < cursor)
                    {
                        skipped.Add(new DesyncSkippedStep(step, DesyncSkipReason.PositionNotAscending));
                        break;
                    }
                    if (oobAt >= bytes.Length)
                    {
                        skipped.Add(new DesyncSkippedStep(step, DesyncSkipReason.PositionOutOfBounds));
                        break;
                    }
                    AddNormal(segments, bytes, cursor, oobAt);
                    segments.Add(new DesyncSegment(bytes.AsMemory(oobAt, 1), DesyncSegmentMode.Urgent,
                        TimeSpan.Zero, oobAt));
                    cursor = oobAt + 1;
                    accepted = true;
                    break;
            }
        }

        // Nothing usable means no desync at all: the caller must fall back to a plain write, not to a plan
        // that happens to contain the untouched hello.
        if (accepted) AddNormal(segments, bytes, cursor, bytes.Length);
        return new DesyncPlan(segments, skipped);
    }

    private static bool TryResolve(ClientHelloLayout layout, DesyncPosition position,
                                   out int offset, out DesyncSkipReason reason)
    {
        if (IsSniBound(position) && !layout.HasSni)
        {
            offset = 0;
            reason = DesyncSkipReason.SniUnavailable;
            return false;
        }
        int? resolved = layout.Resolve(position);
        if (resolved is null)
        {
            offset = 0;
            reason = DesyncSkipReason.PositionOutOfBounds;
            return false;
        }
        offset = resolved.Value;
        reason = default;
        return true;
    }

    private static bool IsSniBound(DesyncPosition position) =>
        position is SniStart or SniEnd or SniMid or SldMid;

    private static void AddNormal(List<DesyncSegment> segments, byte[] bytes, int start, int end) =>
        AddSegment(segments, bytes, start, end, DesyncSegmentMode.Normal);

    private static void AddSegment(List<DesyncSegment> segments, byte[] bytes, int start, int end,
                                   DesyncSegmentMode mode)
    {
        if (end <= start) return;
        segments.Add(new DesyncSegment(bytes.AsMemory(start, end - start), mode, TimeSpan.Zero, start));
    }

    /// <summary>
    /// Cuts the record that contains <paramref name="split"/> into two records by inserting a five-byte
    /// header at the cut and fixing both lengths. Returns false when the cut is not strictly inside a
    /// record payload.
    /// </summary>
    private static bool TryRebuildRecord(byte[] bytes, ClientHelloLayout layout, int split,
                                         out byte[] rebuilt, out ClientHelloLayout rebuiltLayout)
    {
        rebuilt = bytes;
        rebuiltLayout = layout;

        int recordStart = -1;
        int recordEnd = -1;
        int record = 0;
        while (record + 5 <= bytes.Length)
        {
            int length = BinaryPrimitives.ReadUInt16BigEndian(bytes.AsSpan(record + 3, 2));
            int end = record + 5 + length;
            if (end > bytes.Length) return false;
            if (split > record + 5 && split < end)
            {
                recordStart = record;
                recordEnd = end;
                break;
            }
            record = end;
        }
        if (recordStart < 0) return false;

        var result = new byte[bytes.Length + 5];
        bytes.AsSpan(0, split).CopyTo(result);

        int firstPayload = split - recordStart - 5;
        BinaryPrimitives.WriteUInt16BigEndian(result.AsSpan(recordStart + 3, 2), (ushort)firstPayload);

        result[split] = 0x16;
        result[split + 1] = 0x03;
        result[split + 2] = bytes[recordStart + 2];
        int secondPayload = recordEnd - split;
        BinaryPrimitives.WriteUInt16BigEndian(result.AsSpan(split + 3, 2), (ushort)secondPayload);

        bytes.AsSpan(split).CopyTo(result.AsSpan(split + 5));
        rebuilt = result;
        rebuiltLayout = layout.WithRecordSplit(split);
        return true;
    }
}
