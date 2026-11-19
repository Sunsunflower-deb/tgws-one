using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace TgwsOne.Core;

/// <summary>The engine's command line (flags of tg-ws-proxy v1.11.4) and helpers to compare and display it.</summary>
public static class EngineCommand
{
    /// <summary>
    /// Argv in a fixed order. An empty DC map is passed as a bare <c>--dc-ip</c>: without it the engine
    /// falls back to its built-in DC 2/4 list, which would silently undo "clearing the DC list is saved".
    /// </summary>
    public static IReadOnlyList<string> Build(ProxySettings s, AppPaths paths)
    {
        string Num(int n) => n.ToString(CultureInfo.InvariantCulture);
        var a = new List<string>
        {
            "--host", s.Host.ToString(), "--port", Num(s.Port), "--secret", s.Secret,
        };

        if (s.DcMap.Count == 0)
            a.Add("--dc-ip");
        foreach (DcEntry dc in s.DcMap)
        {
            a.Add("--dc-ip");
            a.Add($"{dc.Dc}:{dc.Ip}");
        }

        if (!s.CfProxy) a.Add("--no-cfproxy");
        if (!s.H2) a.Add("--no-h2");
        if (s.NoSecure) a.Add("--no-secure");
        foreach (string d in s.CfDomains) { a.Add("--cfproxy-domain"); a.Add(d); }
        foreach (string d in s.WorkerDomains) { a.Add("--cfproxy-worker-domain"); a.Add(d); }
        if (s.Verbose) a.Add("-v");

        a.AddRange(["--log-file", paths.ProxyLogFile, "--log-max-mb", Num(s.LogMaxMb), "--log-backups", "1"]);
        a.AddRange(["--buf-kb", Num(s.BufKb), "--pool-size", Num(s.PoolSize)]);
        return a;
    }

    /// <summary>SHA-256 (lower-case hex) of the argv joined with NUL, so argument boundaries matter.</summary>
    public static string Hash(IReadOnlyList<string> argv)
        => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(string.Join('\0', argv))));

    /// <summary>A printable command line: the value after <c>--secret</c> is hidden, arguments with spaces are quoted.</summary>
    public static string Mask(IReadOnlyList<string> argv)
    {
        var parts = new List<string>(argv.Count);
        for (int i = 0; i < argv.Count; i++)
        {
            string a = argv[i];
            parts.Add(Quote(a));
            if (a == "--secret" && i + 1 < argv.Count)
            {
                parts.Add("********");
                i++;
            }
        }
        return string.Join(' ', parts);
    }

    private static string Quote(string a) => a.Length == 0 || a.Contains(' ') ? $"\"{a}\"" : a;
}
