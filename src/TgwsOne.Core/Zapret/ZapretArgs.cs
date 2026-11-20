namespace TgwsOne.Core.Zapret;

/// <summary>The four list files a run reads (full paths): the targets are admin-owned, the exclusions service-written.</summary>
public sealed record ZapretLists(string CloudflareIps, string TelegramHosts, string ExcludeHosts, string ExcludeIps);

/// <summary>Builds the winws2/nfqws2 command line for a strategy.</summary>
public static class ZapretArgs
{
    public const int QueueNum = 3443;

    /// <summary>The key of the shared <c>autostate</c> table: both profiles use it, so a host has one strategy.</summary>
    public const string AutoStateKey = "tgws";

    /// <summary>The folder (inside the service's state dir) nfqws2 makes writable for Lua; <c>auto</c> keeps its state file there.</summary>
    public const string WritableFolder = "zapret-state";

    private const string CustomPrefix = "custom:";

    /// <summary>
    /// Layout: [debug], Lua scripts, blobs, interception, then two profiles that differ only in what they target
    /// (all Cloudflare IPs / the Telegram hosts) and carry the same body. Paths are absolute, in the target OS style.
    /// <paramref name="stateDir"/> only matters for <c>auto</c> (the directory Lua may write its state file into).
    /// <paramref name="custom"/> is the loaded desync part when <paramref name="strategyId"/> is <c>custom:&lt;name&gt;</c>.
    /// </summary>
    public static IReadOnlyList<string> Build(OsKind os, ZapretBundle bundle, ZapretLists lists, string strategyId,
                                              string stateDir, DesyncPart? custom = null, string? debugFile = null)
    {
        bool auto = strategyId == BuiltInStrategies.Auto;
        var blobs = new List<Blob>();
        var extraInits = new List<string>();
        var extraBlobs = new List<string>();
        List<string> body;

        if (auto)
        {
            body =
            [
                "--in-range=-s5556",
                "--out-range=-d10",
                "--payload=all",
                $"--lua-desync=circular:fails=3:time=60:key={AutoStateKey}",
                "--in-range=x",
                "--payload=tls_client_hello",
            ];
            for (int i = 0; i < BuiltInStrategies.Rotation.Count; i++)
            {
                DesyncPart part = BuiltInStrategies.Part(BuiltInStrategies.Rotation[i]);
                blobs.AddRange(part.Blobs);
                body.AddRange(part.Args.Select(a => $"{a}:strategy={i + 1}"));
            }
        }
        else
        {
            DesyncPart part;
            if (strategyId.StartsWith(CustomPrefix, StringComparison.Ordinal))
                part = custom ?? throw new ArgumentException($"strategy '{strategyId}' needs its loaded desync part", nameof(custom));
            else
                part = BuiltInStrategies.Part(strategyId);

            blobs.AddRange(part.Blobs);
            body = ["--out-range=-d10", "--payload=tls_client_hello"];
            // The body is repeated in both profiles, but blobs and Lua scripts are global (and nfqws2 refuses a
            // blob name defined twice): lines of a custom strategy that declare them go to the head, once.
            foreach (string arg in part.Args)
            {
                if (arg.StartsWith("--lua-init=", StringComparison.Ordinal)) extraInits.Add(arg);
                else if (arg.StartsWith("--blob=", StringComparison.Ordinal)) extraBlobs.Add(arg);
                else body.Add(arg);
            }
        }

        string Lua(string file) => $"--lua-init=@{OsPath.Join(os, bundle.LuaDir, file)}";

        var argv = new List<string>();
        if (debugFile != null) argv.Add($"--debug=@{debugFile}");
        argv.Add(Lua("zapret-lib.lua"));
        argv.Add(Lua("zapret-antidpi.lua"));
        if (auto)
        {
            argv.Add(Lua("zapret-auto.lua"));
            argv.Add($"--writable={OsPath.Join(os, stateDir, WritableFolder)}");
            argv.Add(Lua("tgws-state.lua"));
        }
        argv.AddRange(extraInits.Distinct());

        foreach (Blob b in blobs.DistinctBy(b => b.Name))
            argv.Add($"--blob={b.Name}:@{OsPath.Join(os, bundle.FakeDir, b.FakeFile)}");
        argv.AddRange(extraBlobs.Distinct());

        argv.Add(os == OsKind.Windows ? "--wf-tcp-out=443" : $"--qnum={QueueNum}");

        argv.AddRange(["--filter-tcp=443", "--filter-l7=tls",
            $"--ipset={lists.CloudflareIps}",
            $"--ipset-exclude={lists.ExcludeIps}",
            $"--hostlist-exclude={lists.ExcludeHosts}"]);
        argv.AddRange(body);
        argv.Add("--new");
        argv.AddRange(["--filter-tcp=443", "--filter-l7=tls",
            $"--hostlist={lists.TelegramHosts}",
            $"--hostlist-exclude={lists.ExcludeHosts}"]);
        argv.AddRange(body);
        return argv;
    }
}
