// Record-driver shim for rust-anything (RT3).  Exposes the public
// `ProxySettingsStore` entry points with primitive signatures so the private
// field parsers are recorded through the behaviour they drive.  Field order is
// fixed and documented below; the Rust port mirrors it.
// Not part of TgwsOne.slnx and not compiled into any product assembly.
using System.Globalization;
using System.Net;
using System.Reflection;
using System.Text;

namespace TgwsOne.Core;

public static class ProxySettingsShims
{
    // `entries` is the parsed INI file in order: "section\u001fkey\u001fvalue"
    // (the Rust IniFile core produces it).  Building the text here keeps the
    // record driven by primitives.
    private const char Sep = '\u001f';

    // ProxyForm: host, port, dcmap, cfproxy, h2, nosecure, verbose, cfdomains,
    // workerdomains, bufkb, poolsize, logmaxmb, faketls.
    public static string[] ReadForm(string[] entries)
    {
        ProxyForm form = ProxySettingsStore.ReadForm(LoadIni(entries));
        return
        [
            form.Host, form.Port, form.DcMap,
            form.CfProxy ? "1" : "0", form.H2 ? "1" : "0", form.NoSecure ? "1" : "0", form.Verbose ? "1" : "0",
            form.CfDomains, form.WorkerDomains, form.BufKb, form.PoolSize, form.LogMaxMb, form.FakeTlsDomain,
        ];
    }

    // ProxySettings: host, port, dcmap, flags, domains, ints, secret, faketls.
    public static string[] Load(string[] entries) => SettingsFields(ProxySettingsStore.Load(LoadIni(entries)));

    // Returns ["ok", ...settings] or ["err", "Field\u001fmessage", ...].
    public static string[] Parse(
        string host, string port, string dcMap, bool cfProxy, bool h2, bool noSecure, bool verbose,
        string cfDomains, string workerDomains, string bufKb, string poolSize, string logMaxMb,
        string fakeTlsDomain, string secret)
    {
        var form = new ProxyForm(host, port, dcMap, cfProxy, h2, noSecure, verbose, cfDomains, workerDomains,
            bufKb, poolSize, logMaxMb, fakeTlsDomain);
        (ProxySettings? settings, IReadOnlyList<FieldError> errors) = ProxySettingsStore.Parse(form, secret);
        if (settings is null)
        {
            return ["err", .. errors.Select(e => $"{e.Field}\u001f{e.Message}")];
        }
        return ["ok", .. SettingsFields(settings)];
    }

    public static bool IsValidSecret(string? secret) => ProxySettingsStore.IsValidSecret(secret);

    public static string[] SplitList(string? raw) => ProxySettingsStore.SplitList(raw).ToArray();

    // ---------- field parsers, reached through the private static methods ----------

    private static MethodInfo Private(string name)
        => typeof(ProxySettingsStore).GetMethod(name, BindingFlags.NonPublic | BindingFlags.Static)!;

    // ["ok", domain] or ["bad"].
    public static string[] TryDomain(string raw)
    {
        object?[] args = [raw, null];
        bool ok = (bool)Private(nameof(TryDomain)).Invoke(null, args)!;
        return ok ? ["ok", (string)args[1]!] : ["bad"];
    }

    public static string NormalizeDomain(string raw)
        => (string)Private(nameof(NormalizeDomain)).Invoke(null, [raw])!;

    // ["ok", "dotted-ip"] or ["bad"].
    public static string[] TryHost(string raw)
    {
        object?[] args = [raw, null];
        bool ok = (bool)Private(nameof(TryHost)).Invoke(null, args)!;
        return ok ? ["ok", ((IPAddress)args[1]!).ToString()] : ["bad"];
    }

    // ["ok", number] or ["bad"].
    public static string[] TryPort(string raw)
    {
        object?[] args = [raw, null];
        bool ok = (bool)Private(nameof(TryPort)).Invoke(null, args)!;
        return ok ? ["ok", ((int)args[1]!).ToString(CultureInfo.InvariantCulture)] : ["bad"];
    }

    // ["ok", number] or ["bad"].
    public static string[] TryInt(string raw, int min)
    {
        object?[] args = [raw, min, null];
        bool ok = (bool)Private(nameof(TryInt)).Invoke(null, args)!;
        return ok ? ["ok", ((int)args[2]!).ToString(CultureInfo.InvariantCulture)] : ["bad"];
    }

    // True for "1", "true", "yes" (any case); an empty value asks for the default.
    public static bool ParseBool(string? value, bool fallback)
        => (bool)Private(nameof(ParseBool)).Invoke(null, [value, fallback])!;

    // ["ok", "dc:ip", ...] or ["err", "Field\u001fmessage"].
    public static string[] ParseDcMap(string raw)
    {
        object?[] args = [raw, null];
        var map = (List<DcEntry>?)Private(nameof(ParseDcMap)).Invoke(null, args);
        if (map is null)
        {
            var error = (FieldError)args[1]!;
            return ["err", $"{error.Field}\u001f{error.Message}"];
        }
        return ["ok", .. map.Select(d => $"{d.Dc}:{d.Ip}")];
    }

    // ---------- encoding helpers ----------

    private static string[] SettingsFields(ProxySettings s)
        =>
        [
            s.Host.ToString(), s.Port.ToString(CultureInfo.InvariantCulture),
            string.Join(",", s.DcMap.Select(d => $"{d.Dc}:{d.Ip}")),
            s.CfProxy ? "1" : "0", s.H2 ? "1" : "0", s.NoSecure ? "1" : "0", s.Verbose ? "1" : "0",
            string.Join(",", s.CfDomains), string.Join(",", s.WorkerDomains),
            s.BufKb.ToString(CultureInfo.InvariantCulture),
            s.PoolSize.ToString(CultureInfo.InvariantCulture),
            s.LogMaxMb.ToString(CultureInfo.InvariantCulture),
            s.Secret, s.FakeTlsDomain,
        ];

    private static IniFile LoadIni(string[] entries)
    {
        var sb = new StringBuilder();
        foreach (string entry in entries)
        {
            string[] parts = entry.Split(Sep);
            if (parts.Length != 3) continue;   // malformed random input
            sb.Append('[').Append(parts[0]).Append("]\n").Append(parts[1]).Append('=').Append(parts[2]).Append('\n');
        }
        string dir = Path.Combine(Path.GetTempPath(), "rt3-proxy-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        string path = Path.Combine(dir, "state.ini");
        File.WriteAllText(path, sb.ToString(), new UTF8Encoding(false));
        IniFile ini = IniFile.Load(path);
        Directory.Delete(dir, true);
        return ini;
    }
}
