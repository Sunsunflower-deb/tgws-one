using System.Text;

namespace TgwsOne.Core;

/// <summary>
/// INI file in the 1.x format: <c>[section]</c>, <c>key=value</c>, <c>;</c>/<c>#</c> comments.
/// Section and key names are case-insensitive (stored lower-case). Key order, empty values and
/// unknown sections survive a load/save round trip; comments do not (same as 1.x).
/// </summary>
public sealed class IniFile
{
    private sealed class Entry(string key, string value)
    {
        public string Key { get; } = key;
        public string Value { get; set; } = value;
    }

    private sealed class Section(string name)
    {
        public string Name { get; } = name;
        public List<Entry> Items { get; } = [];
    }

    private readonly List<Section> _sections = [];

    private IniFile(string path) => Path = path;

    public string Path { get; }

    /// <summary>Reads the file; a missing file gives an empty instance.</summary>
    public static IniFile Load(string path)
    {
        var ini = new IniFile(path);
        if (!File.Exists(path)) return ini;

        string section = "app"; // keys before any header belong to [app], as in 1.x
        foreach (string raw in File.ReadAllLines(path, Encoding.UTF8))
        {
            string line = raw.Trim();
            if (line.Length == 0 || line[0] == ';' || line[0] == '#') continue;
            if (line[0] == '[' && line[^1] == ']')
            {
                section = line[1..^1].Trim().ToLowerInvariant();
                continue;
            }
            int eq = line.IndexOf('=');
            if (eq <= 0) continue;
            ini.Put(section, line[..eq].Trim().ToLowerInvariant(), line[(eq + 1)..].Trim());
        }
        return ini;
    }

    /// <summary>The raw value (including an empty one), or null when the key is absent.</summary>
    public string? Get(string section, string key)
        => Find(section.ToLowerInvariant(), key.ToLowerInvariant())?.Value;

    /// <summary>Sets a value. A new key goes to the end of its section, a new section to the end of the file.</summary>
    public void Set(string section, string key, string value)
    {
        if (value.Contains('\n') || value.Contains('\r'))
            throw new ArgumentException("INI value must be a single line", nameof(value));
        Put(section.ToLowerInvariant(), key.ToLowerInvariant(), value);
    }

    /// <summary>A snapshot of every (section, key, value) in file order.</summary>
    public IEnumerable<(string Section, string Key, string Value)> Entries()
        => _sections.SelectMany(s => s.Items.Select(e => (s.Name, e.Key, e.Value))).ToList();

    /// <summary>
    /// Writes UTF-8 without BOM. Goes through a temp file and a rename so a concurrent reader
    /// (the CLI and the GUI share this file) never sees a half-written INI. The directory is made private
    /// (0700 on Linux) unless <paramref name="privateDirectory"/> is false: the bypass service keeps its state in
    /// a folder that nfqws2 must be able to reach too (see <see cref="PublicDirectory"/>).
    /// </summary>
    public void Save(bool privateDirectory = true)
    {
        string? dir = System.IO.Path.GetDirectoryName(Path);
        if (!string.IsNullOrEmpty(dir))
        {
            if (privateDirectory) PrivateDirectory.Ensure(dir);
            else PublicDirectory.Ensure(dir);
        }

        var sb = new StringBuilder();
        bool first = true;
        foreach (Section s in _sections)
        {
            if (s.Items.Count == 0) continue;
            if (!first) sb.Append(Environment.NewLine);
            first = false;
            sb.Append('[').Append(s.Name).Append(']').Append(Environment.NewLine);
            foreach (Entry e in s.Items)
                sb.Append(e.Key).Append('=').Append(e.Value).Append(Environment.NewLine);
        }

        string tmp = $"{Path}.{Environment.ProcessId}.tmp";
        File.WriteAllText(tmp, sb.ToString(), new UTF8Encoding(false));
        try
        {
            File.Move(tmp, Path, overwrite: true);
        }
        catch
        {
            try { File.Delete(tmp); } catch { /* best effort */ }
            throw;
        }
    }

    private Entry? Find(string section, string key)
        => _sections.FirstOrDefault(s => s.Name == section)?.Items.FirstOrDefault(e => e.Key == key);

    private void Put(string section, string key, string value)
    {
        Section? sec = _sections.FirstOrDefault(s => s.Name == section);
        if (sec == null)
        {
            sec = new Section(section);
            _sections.Add(sec);
        }
        Entry? e = sec.Items.FirstOrDefault(x => x.Key == key);
        if (e == null) sec.Items.Add(new Entry(key, value));
        else e.Value = value;
    }
}
