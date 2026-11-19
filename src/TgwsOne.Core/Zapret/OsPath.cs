namespace TgwsOne.Core.Zapret;

/// <summary>Joins path parts with the target OS separator, so a Windows layout can be built (and tested) on Linux.</summary>
internal static class OsPath
{
    public static char Separator(OsKind os) => os == OsKind.Windows ? '\\' : '/';

    public static string Join(OsKind os, string first, params string[] rest)
    {
        char sep = Separator(os);
        string r = first;
        foreach (string part in rest)
            r = (r.EndsWith(sep) ? r : r + sep) + part;
        return r;
    }
}
