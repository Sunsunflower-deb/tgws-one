namespace TgwsOne.Core.Tests.Zapret;

/// <summary>Finds the repository checkout the tests run from (walking up from the test binaries).</summary>
internal static class TestRepo
{
    public static string? Root { get; } = FindRoot();

    /// <summary>The unpacked Linux zapret2 bundle (<c>make zapret2</c>), or null when it isn't there.</summary>
    public static string? Zapret2Linux
        => Root is { } r && File.Exists(Path.Combine(r, "out", "zapret2", "linux-x64", "nfqws2"))
            ? Path.Combine(r, "out", "zapret2", "linux-x64")
            : null;

    /// <summary>A value from <c>zapret2/zapret2.lock</c>.</summary>
    public static string LockValue(string key)
    {
        string prefix = key + "=";
        string line = File.ReadLines(Path.Combine(Root!, "zapret2", "zapret2.lock"))
            .First(l => l.StartsWith(prefix, StringComparison.Ordinal));
        return line[prefix.Length..].Trim();
    }

    private static string? FindRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        for (; dir != null; dir = dir.Parent)
            if (File.Exists(Path.Combine(dir.FullName, "zapret2", "zapret2.lock"))) return dir.FullName;
        return null;
    }
}
