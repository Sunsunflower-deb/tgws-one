namespace TgwsOne.Core.Zapret;

/// <summary>A fake ClientHello (a file inside <c>&lt;bundle&gt;/files/fake</c>) preloaded under a name Lua steps can refer to.</summary>
public sealed record Blob(string Name, string FakeFile);

/// <summary>
/// The part of a strategy that differs: blobs to preload and the <c>--lua-desync</c> steps (full arguments).
/// The target filters and the packet range options around it are the same for every strategy.
/// </summary>
public sealed record DesyncPart(IReadOnlyList<Blob> Blobs, IReadOnlyList<string> Args);

/// <summary>
/// The five built-in strategies. The four plain ones are ports of the 1.x zapret1 presets; <c>auto</c> rotates
/// them per host with zapret2's <c>circular</c> orchestrator (the argument builder wires that up).
/// </summary>
public static class BuiltInStrategies
{
    public const string Auto = "auto";
    public const string FakeTls = "fake-tls";
    public const string Split = "split";
    public const string SplitStealth = "split-stealth";
    public const string Aggressive = "aggressive";

    private static readonly Blob Google = new("tls_google", "tls_clienthello_www_google_com.bin");
    // 1.x pointed split-stealth at a 4pda.to ClientHello that zapret2 doesn't ship; vk.com stands in for it.
    private static readonly Blob Vk = new("tls_vk", "tls_clienthello_vk_com.bin");

    public static IReadOnlyList<string> Ids { get; } = [Auto, FakeTls, Split, SplitStealth, Aggressive];

    /// <summary>What <c>auto</c> cycles through, in order: the position + 1 is the strategy number tagged on each step.</summary>
    public static IReadOnlyList<string> Rotation { get; } = [FakeTls, Split, SplitStealth, Aggressive];

    private static readonly Dictionary<string, DesyncPart> Parts = new(StringComparer.Ordinal)
    {
        [FakeTls] = new([],
        [
            "--lua-desync=fake:blob=0x00000000:tcp_seq=-10000:repeats=11",
            "--lua-desync=fake:blob=fake_default_tls:tcp_seq=-10000:repeats=11:tls_mod=rnd,dupsid,sni=www.google.com",
            "--lua-desync=multidisorder:pos=1,midsld",
        ]),
        [Split] = new([Google], ["--lua-desync=multisplit:pos=1:seqovl=681:seqovl_pattern=tls_google"]),
        [SplitStealth] = new([Vk], ["--lua-desync=multisplit:pos=1:seqovl=568:seqovl_pattern=tls_vk"]),
        [Aggressive] = new([Google],
        [
            "--lua-desync=fake:blob=tls_google:tcp_md5:repeats=11",
            "--lua-desync=multisplit:pos=1",
        ]),
    };

    public static bool IsBuiltIn(string id) => Ids.Contains(id, StringComparer.Ordinal);

    /// <summary>The desync part of a plain strategy. <c>auto</c> has none of its own: it is built from <see cref="Rotation"/>.</summary>
    public static DesyncPart Part(string id)
        => Parts.TryGetValue(id, out DesyncPart? part)
            ? part
            : throw new ArgumentException($"no desync part for strategy '{id}'", nameof(id));
}
