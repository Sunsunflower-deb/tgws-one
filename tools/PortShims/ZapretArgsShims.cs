// Record/golden driver shim for rust-anything (RT4).  The original `Build`
// takes an enum, two classes and optional lists, which the bridge cannot take;
// this adapter exposes it through strings and arrays.  The output is the exact
// argv the original would hand to nfqws2/winws2.
// Not part of TgwsOne.slnx and not compiled into any product assembly.

namespace TgwsOne.Core.Zapret;

public static class ZapretArgsShims
{
    // os: "windows" or anything else (Linux).  appDir is the application folder;
    // `<appDir>/zapret2` becomes the bundle unless TGWS_ZAPRET_DIR is set.
    // customArgs is null for a built-in strategy; rotation null/empty means the
    // universal rotation.
    public static string[] Build(
        string os,
        string appDir,
        string strategyId,
        string cloudflareIps,
        string telegramHosts,
        string excludeHosts,
        string excludeIps,
        string cfProxyHosts,
        string stateDir,
        string scope,
        string[]? customArgs,
        string[]? rotation,
        string? debugFile)
    {
        OsKind kind = os == "windows" ? OsKind.Windows : OsKind.Linux;
        ZapretBundle bundle = ZapretBundle.Locate(kind, appDir, _ => null);
        var lists = new ZapretLists(cloudflareIps, telegramHosts, excludeHosts, excludeIps, cfProxyHosts);
        DesyncPart? custom = customArgs is null ? null : new DesyncPart([], customArgs);
        return
        [
            .. ZapretArgs.Build(kind, bundle, lists, strategyId, stateDir, custom, debugFile,
                ZapretScopes.Parse(scope), rotation),
        ];
    }

    // "all" or "telegram" (an unknown value must stay narrow), same as the enum wire form.
    public static string ScopeParse(string? value) => ZapretScopes.Parse(value).ToWire();
}
