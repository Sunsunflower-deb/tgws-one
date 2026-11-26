// Record-driver shim for rust-anything (RT3).  The original returns a tuple of
// two lists; the bridge cannot take a tuple, so each result is encoded as
// "V\u001fvalue" (valid) or "I\u001fvalue" (invalid), in the original order.
// Not part of TgwsOne.slnx and not compiled into any product assembly.

namespace TgwsOne.Core.Zapret;

public static class ExclusionsShims
{
    public static string[] NormalizeHosts(string[] raw) => Encode(Exclusions.NormalizeHosts(raw));

    public static string[] NormalizeIps(string[] raw) => Encode(Exclusions.NormalizeIps(raw));

    private static string[] Encode((IReadOnlyList<string> Valid, IReadOnlyList<string> Invalid) result)
        =>
        [
            .. result.Valid.Select(v => "V\u001f" + v),
            .. result.Invalid.Select(i => "I\u001f" + i),
        ];
}
