namespace TgwsOne.Engine.Desync;

/// <summary>A step applied to the first ClientHello of an outgoing TLS connection.</summary>
public abstract record DesyncStep;

/// <summary>Cut the flight at <paramref name="Pos"/>: the two halves go out as separate writes.</summary>
public sealed record Split(DesyncPosition Pos) : DesyncStep;

/// <summary>Send the part before <paramref name="Pos"/> with a low TTL, then the rest normally.</summary>
public sealed record Disorder(DesyncPosition Pos) : DesyncStep;

/// <summary>Replace the part before <paramref name="Pos"/> with fake bytes; the real bytes follow.</summary>
public sealed record Fake(DesyncPosition Pos, int? Ttl, string? Sni) : DesyncStep;

/// <summary>Send the byte at <paramref name="Pos"/> as TCP urgent data.</summary>
public sealed record Oob(DesyncPosition Pos) : DesyncStep;

/// <summary>Cut the TLS record at <paramref name="Pos"/> and write the two halves as two records.</summary>
public sealed record TlsRec(DesyncPosition Pos) : DesyncStep;

/// <summary>Wait before the next segment is sent.</summary>
public sealed record Pause(TimeSpan Delay) : DesyncStep;

/// <summary>An ordered list of steps for one route. An empty list means no desync.</summary>
public sealed record DesyncStrategy(string Id, IReadOnlyList<DesyncStep> Steps)
{
    /// <summary>The user-facing name; built-in strategies have Russian titles, custom ones may leave it null.</summary>
    public string? Title { get; init; }
}

/// <summary>Why an egress attempt failed, as far as the desync controller can tell.</summary>
public enum EgressFailure
{
    None, ConnectTimeout, ConnectRefused, TlsTimeout, TlsReset, TlsAlert, UpgradeRejected, Io, Unsupported
}

/// <summary>Picks a strategy for a route and receives the outcome of every attempt that used one.</summary>
public interface IDesyncController
{
    /// <summary>Null means no desync for this route.</summary>
    DesyncStrategy? Resolve(string sni, string route);

    void Report(string sni, string route, string? strategyId, bool ok, EgressFailure failure, TimeSpan elapsed);
}

/// <summary>The platform primitives the planner may rely on. A step needing a missing flag is skipped.</summary>
[Flags]
public enum DesyncCapabilities
{
    None = 0,
    LowTtl = 1,
    SpliceFake = 2,
    Urgent = 4,
    All = LowTtl | SpliceFake | Urgent,
}

/// <summary>How the sender must put a segment on the wire.</summary>
public enum DesyncSegmentMode
{
    Normal,
    LowTtl,
    FakeSplice,
    Urgent,
}

/// <summary>
/// One unit of the send plan. <see cref="Offset"/> is where the segment starts in the rebuilt first flight;
/// <see cref="Length"/> is the number of bytes it covers. A <see cref="DesyncSegmentMode.FakeSplice"/>
/// segment carries placeholder bytes and <see cref="NeedsFakeData"/> tells the sender to build the fake
/// ClientHello for exactly that range before sending it.
/// </summary>
public sealed record DesyncSegment(
    ReadOnlyMemory<byte> Bytes,
    DesyncSegmentMode Mode,
    TimeSpan Delay,
    int Offset = 0,
    bool NeedsFakeData = false,
    string? FakeSni = null,
    int? FakeTtl = null)
{
    public int Length => Bytes.Length;
}

/// <summary>Why the planner could not use a step.</summary>
public enum DesyncSkipReason
{
    /// <summary>The platform lacks the primitive the step needs.</summary>
    Unsupported,

    /// <summary>The resolved offset is before the previous accepted cut.</summary>
    PositionNotAscending,

    /// <summary>The resolved offset is outside the buffer.</summary>
    PositionOutOfBounds,

    /// <summary>The step is bound to SNI, but the ClientHello has no SNI entry.</summary>
    SniUnavailable,

    /// <summary>The step does not change anything (for example a zero-length fake).</summary>
    Empty,
}

/// <summary>A step that was left out of the plan, with the reason.</summary>
public sealed record DesyncSkippedStep(DesyncStep Step, DesyncSkipReason Reason);

/// <summary>The send plan and the steps that could not be used. An empty segment list means no desync.</summary>
public sealed record DesyncPlan(
    IReadOnlyList<DesyncSegment> Segments,
    IReadOnlyList<DesyncSkippedStep> Skipped)
{
    public bool IsEmpty => Segments.Count == 0;
}
