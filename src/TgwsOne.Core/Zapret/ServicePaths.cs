using System.Security.Cryptography;
using System.Text;

namespace TgwsOne.Core.Zapret;

/// <summary>
/// Where the privileged service keeps its files and listens. Nothing here touches the disk except the two
/// <c>Seed…/Ensure…</c> methods the service calls on start. Windows keeps 1.x's <c>ProgramData\TgwsOne\lists</c>
/// folder, so existing exclusions survive the upgrade.
/// </summary>
public sealed class ServicePaths
{
    /// <summary>The first line of both exclusion files the service writes.</summary>
    public const string ExclusionsHeader = "# Исключения обхода DPI — правятся из приложения TgwsOne";

    private const string PipeName = "TgwsOne.Zapret";
    private const string TelegramFile = "telegram.txt";
    private const string CloudflareFile = "cloudflare-ips.txt";

    private ServicePaths(OsKind os, Func<string, string?> env)
    {
        string Join(string first, params string[] rest) => OsPath.Join(os, first, rest);

        if (env("TGWS_SERVICE_ROOT") is { Length: > 0 } root)
        {
            StateIni = Join(root, "state.ini");
            ListsDir = Join(root, "lists");
            ExclusionsDir = Join(root, "exclusions");
            StrategiesDir = Join(root, "strategies");
            LogsDir = Join(root, "logs");
            StateDir = Join(root, "state");
            // Parallel test roots must not share one pipe.
            Endpoint = os == OsKind.Windows ? $"{PipeName}.{RootHash(root)}" : Join(root, "control.sock");
            WindowsRoot = os == OsKind.Windows ? root : null;
        }
        else if (os == OsKind.Windows)
        {
            string programData = env("ProgramData") is { Length: > 0 } pd ? pd : @"C:\ProgramData";
            string winRoot = Join(programData, "TgwsOne");
            StateIni = Join(winRoot, "service.ini");
            ListsDir = Join(winRoot, "lists");
            ExclusionsDir = ListsDir;
            StrategiesDir = Join(winRoot, "strategies");
            LogsDir = Join(winRoot, "logs");
            StateDir = Join(winRoot, "state");
            Endpoint = PipeName;
            WindowsRoot = winRoot;
        }
        else
        {
            StateIni = "/var/lib/tgwsone/state.ini";
            ListsDir = "/etc/tgwsone/lists";
            ExclusionsDir = "/var/lib/tgwsone";
            StrategiesDir = "/etc/tgwsone/strategies";
            LogsDir = "/var/log/tgwsone";
            StateDir = "/var/lib/tgwsone";
            Endpoint = "/run/tgwsone/control.sock";
            WindowsRoot = null;
        }

        Lists = new ZapretLists(
            Join(ListsDir, CloudflareFile), Join(ListsDir, TelegramFile),
            Join(ExclusionsDir, "exclude-hosts.txt"), Join(ExclusionsDir, "exclude-ips.txt"));
    }

    public static ServicePaths For(OsKind os, Func<string, string?> env) => new(os, env);

    /// <summary>The service's own state: <c>enabled</c> and the chosen strategy.</summary>
    public string StateIni { get; }
    /// <summary>Target lists (Telegram hosts, Cloudflare ranges): admin-owned.</summary>
    public string ListsDir { get; }
    /// <summary>Exclusion lists: written by the service on behalf of any user.</summary>
    public string ExclusionsDir { get; }
    /// <summary>Hand-written <c>&lt;name&gt;.args</c> strategies: admin-owned.</summary>
    public string StrategiesDir { get; }
    public string LogsDir { get; }
    /// <summary>The service's scratch space (the Lua state file of <c>auto</c> goes into a folder below it).</summary>
    public string StateDir { get; }
    /// <summary>Windows: the pipe name. Linux: the Unix socket path.</summary>
    public string Endpoint { get; }
    /// <summary>Windows: the folder whose ACL the service tightens and where 1.x left its files; null on Linux.</summary>
    public string? WindowsRoot { get; }
    public ZapretLists Lists { get; }

    /// <summary>Copies the two target lists from <paramref name="appListsDir"/> where they are missing; never overwrites.</summary>
    public void SeedTargetLists(string appListsDir)
    {
        Directory.CreateDirectory(ListsDir);
        foreach (string name in new[] { TelegramFile, CloudflareFile })
        {
            string target = Path.Combine(ListsDir, name);
            string source = Path.Combine(appListsDir, name);
            if (!File.Exists(target) && File.Exists(source)) File.Copy(source, target);
        }
    }

    /// <summary>Creates both exclusion files (a header, no entries) where they are missing.</summary>
    public void EnsureExclusionFiles()
    {
        foreach (string file in new[] { Lists.ExcludeHosts, Lists.ExcludeIps })
            if (!File.Exists(file)) Exclusions.Write(file, [], ExclusionsHeader);
    }

    private static string RootHash(string root)
        => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(root)))[..8].ToLowerInvariant();
}
