namespace TgwsOne.Engine.Desync;

/// <summary>
/// The strategies the engine ships with. IDs are stable and Latin; the titles are what the mobile UI shows. The
/// order of <see cref="BuiltIn"/> is the universal <c>auto</c> rotation, so it follows the table row by row.
/// </summary>
public static class BuiltInDesync
{
    public const string Fake = "fake";
    public const string SplitSni = "split-sni";
    public const string SplitMidSni = "split-midsni";
    public const string FakeSplit = "fake-split";
    public const string Disorder1 = "disorder-1";
    public const string DisorderSld = "disorder-sld";
    public const string OobSni = "oob-sni";
    public const string TlsRecMidSni = "tlsrec-midsni";

    private const string GoogleSni = "www.google.com";

    /// <summary>The universal rotation, in the exact table order.</summary>
    public static IReadOnlyList<DesyncStrategy> BuiltIn { get; } =
    [
        new DesyncStrategy(Fake,
        [
            // The whole hello is faked; the low TTL keeps the fake off the server and the retransmission carries
            // the real bytes from the retained pages.
            new Fake(new FromEnd(0), 8, GoogleSni),
        ])
        { Title = "Фейковый ClientHello" },

        new DesyncStrategy(SplitSni,
        [
            new Split(new SniEnd(1)),
        ])
        { Title = "Разрез после имени" },

        new DesyncStrategy(SplitMidSni,
        [
            new Split(new SniMid()),
            new Split(new SniEnd(1)),
        ])
        { Title = "Разрез внутри имени" },

        new DesyncStrategy(FakeSplit,
        [
            new Fake(new SniEnd(1), null, null),
            new Split(new SniEnd(1)),
        ])
        { Title = "Фейк и разрез" },

        new DesyncStrategy(Disorder1,
        [
            new Disorder(new Absolute(1)),
        ])
        { Title = "Разлад первого байта" },

        new DesyncStrategy(DisorderSld,
        [
            new Disorder(new SldMid()),
            new Disorder(new SniEnd(1)),
        ])
        { Title = "Разлад внутри имени" },

        new DesyncStrategy(OobSni,
        [
            new Oob(new SniMid()),
        ])
        { Title = "Срочный байт в имени" },

        new DesyncStrategy(TlsRecMidSni,
        [
            new TlsRec(new SniMid()),
        ])
        { Title = "Две TLS-записи" },
    ];

    public static IReadOnlyList<string> Ids { get; } = [.. BuiltIn.Select(strategy => strategy.Id)];

    public static bool IsBuiltIn(string? id) => id is not null && Ids.Contains(id, StringComparer.Ordinal);

    public static DesyncStrategy? Find(string? id)
        => id is null ? null : BuiltIn.FirstOrDefault(strategy => strategy.Id == id);
}
