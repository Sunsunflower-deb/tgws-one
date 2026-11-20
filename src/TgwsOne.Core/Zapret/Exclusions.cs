using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.RegularExpressions;

namespace TgwsOne.Core.Zapret;

/// <summary>
/// The user-editable exclusions: hosts and IPs the bypass must never touch. Any user may change them through the
/// service, so every entry is checked before it reaches a file that nfqws2/winws2 (running as root / SYSTEM) parses.
/// </summary>
public static partial class Exclusions
{
    public const int MaxEntries = 1000;

    private const int MaxHostLength = 253;

    [GeneratedRegex(@"^[a-z0-9]([a-z0-9-]{0,61}[a-z0-9])?\z", RegexOptions.CultureInvariant)]
    private static partial Regex LabelRegex();

    [GeneratedRegex(@"^\d{1,3}(\.\d{1,3}){3}\z", RegexOptions.CultureInvariant)]
    private static partial Regex DottedQuadRegex();

    [GeneratedRegex(@"^\d{1,3}\z", RegexOptions.CultureInvariant)]
    private static partial Regex PrefixRegex();

    /// <summary>
    /// Trim, lower-case, IDN → punycode; blank entries are ignored. <c>Invalid</c> holds the rejected entries as
    /// typed (trimmed) plus any valid ones beyond <see cref="MaxEntries"/>, in input order.
    /// </summary>
    public static (IReadOnlyList<string> Valid, IReadOnlyList<string> Invalid) NormalizeHosts(IEnumerable<string> raw)
        => Normalize(raw, NormalizeHost);

    /// <summary>A single IPv4/IPv6 address or a CIDR network (host bits cleared), in canonical text.</summary>
    public static (IReadOnlyList<string> Valid, IReadOnlyList<string> Invalid) NormalizeIps(IEnumerable<string> raw)
        => Normalize(raw, NormalizeIp);

    /// <summary>The entries of a list file; comments (<c>#</c>) and blank lines are skipped, a missing file is empty.</summary>
    public static IReadOnlyList<string> Read(string file)
    {
        if (!File.Exists(file)) return [];
        var items = new List<string>();
        foreach (string raw in File.ReadAllLines(file, Encoding.UTF8))
        {
            string line = raw.Trim();
            if (line.Length > 0 && line[0] != '#') items.Add(line);
        }
        return items;
    }

    /// <summary>
    /// Writes the header (each line made a comment) and the items, UTF-8 without BOM, via a temp file and a rename
    /// so the running process never reads a half-written list. nfqws2 re-reads the file after dropping root
    /// privileges, so the folder and the file are made readable for everyone whatever the umask is.
    /// </summary>
    public static void Write(string file, IReadOnlyList<string> items, string headerComment)
    {
        string? dir = Path.GetDirectoryName(file);
        if (!string.IsNullOrEmpty(dir)) PublicDirectory.Ensure(dir);

        var sb = new StringBuilder();
        foreach (string line in headerComment.Split('\n'))
        {
            string text = line.TrimEnd('\r');
            sb.Append(text.StartsWith('#') ? text : "# " + text).Append('\n');
        }
        foreach (string item in items) sb.Append(item).Append('\n');

        string tmp = $"{file}.{Environment.ProcessId}.tmp";
        File.WriteAllText(tmp, sb.ToString(), new UTF8Encoding(false));
        try
        {
            if (!OperatingSystem.IsWindows())
                File.SetUnixFileMode(tmp, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead | UnixFileMode.OtherRead);
            File.Move(tmp, file, overwrite: true);
        }
        catch
        {
            try { File.Delete(tmp); } catch { /* best effort */ }
            throw;
        }
    }

    private static (IReadOnlyList<string> Valid, IReadOnlyList<string> Invalid) Normalize(IEnumerable<string> raw, Func<string, string?> normalize)
    {
        var valid = new List<string>();
        var invalid = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (string entry in raw)
        {
            string text = entry.Trim();
            if (text.Length == 0) continue;
            string? norm = normalize(text);
            if (norm == null) invalid.Add(text);
            else if (!seen.Add(norm)) continue;                 // a repeat is dropped, not an error
            else if (valid.Count >= MaxEntries) invalid.Add(text);
            else valid.Add(norm);
        }
        return (valid, invalid);
    }

    private static string? NormalizeHost(string text)
    {
        string ascii;
        try
        {
            ascii = new IdnMapping().GetAscii(text.ToLowerInvariant());
        }
        catch (ArgumentException)
        {
            return null;
        }
        ascii = ascii.ToLowerInvariant();   // the IDN step may leave upper-case ASCII alone

        if (ascii.Length > MaxHostLength) return null;
        string[] labels = ascii.Split('.');
        if (labels.Length < 2) return null;
        if (!labels.All(l => LabelRegex().IsMatch(l))) return null;
        // No top-level domain is all digits: such an entry is an IP address typed into the wrong list.
        if (labels[^1].All(char.IsAsciiDigit)) return null;
        return ascii;
    }

    private static string? NormalizeIp(string text)
    {
        int slash = text.IndexOf('/');
        string addrText = slash < 0 ? text : text[..slash];
        if (!TryParseAddress(addrText, out IPAddress? addr)) return null;

        int maxPrefix = addr.AddressFamily == AddressFamily.InterNetwork ? 32 : 128;
        if (slash < 0) return addr.ToString();

        string prefixText = text[(slash + 1)..];
        if (!PrefixRegex().IsMatch(prefixText)) return null;
        int prefix = int.Parse(prefixText, CultureInfo.InvariantCulture);
        if (prefix > maxPrefix) return null;
        return $"{Mask(addr, prefix)}/{prefix}";
    }

    // IPAddress.TryParse also takes "1", "10.1", scope ids and the like: accept only the plain textual forms.
    private static bool TryParseAddress(string text, out IPAddress addr)
    {
        addr = IPAddress.None;
        bool plain = text.Contains(':')
            ? text.All(c => char.IsAsciiHexDigit(c) || c is ':' or '.')
            : DottedQuadRegex().IsMatch(text);
        return plain && IPAddress.TryParse(text, out addr!);
    }

    private static IPAddress Mask(IPAddress addr, int prefix)
    {
        byte[] bytes = addr.GetAddressBytes();
        for (int i = 0; i < bytes.Length; i++)
        {
            int keep = Math.Clamp(prefix - i * 8, 0, 8);
            bytes[i] &= (byte)(0xFF << (8 - keep));
        }
        return new IPAddress(bytes);
    }
}
