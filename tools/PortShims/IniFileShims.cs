// Record-driver shim for rust-anything (RT3).  The original `IniFile` only exposes
// `Load`/`Save` (file I/O); the Rust port keeps the pure parse + serialize core.
// This file is deliberately outside TgwsOne.slnx and is not compiled into any
// product assembly: rust-anything compiles it next to the unit when recording.
using System.Text;

namespace TgwsOne.Core;

public static class IniFileShims
{
    private const char Sep = '\u001f';

    /// <summary>Loads <paramref name="text"/> through the real <see cref="IniFile.Load"/> and writes it back with the real <see cref="IniFile.Save"/>, returning the file contents.</summary>
    public static string LoadSave(string text) => Run(text, ini => { });

    /// <summary>Same as <see cref="LoadSave"/> after applying <see cref="IniFile.Set"/>; each item is <c>section\u001fkey\u001fvalue</c>.</summary>
    public static string Mutate(string text, string[] sets)
        => Run(text, ini =>
        {
            foreach (string set in sets)
            {
                string[] parts = set.Split(Sep);
                if (parts.Length != 3) continue;   // malformed random input: the shim skips it
                ini.Set(parts[0], parts[1], parts[2]);
            }
        });

    /// <summary>Every entry in file order, each encoded as <c>section\u001fkey\u001fvalue</c>.</summary>
    public static string[] Entries(string text)
    {
        IniFile ini = LoadFromText(text, out string dir);
        try
        {
            return ini.Entries().Select(e => $"{e.Section}{Sep}{e.Key}{Sep}{e.Value}").ToArray();
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    private static string Run(string text, Action<IniFile> edit)
    {
        IniFile ini = LoadFromText(text, out string dir);
        try
        {
            edit(ini);
            ini.Save();
            return File.ReadAllText(ini.Path, Encoding.UTF8);
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    private static IniFile LoadFromText(string text, out string dir)
    {
        dir = Path.Combine(Path.GetTempPath(), "rt3-ini-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        string path = Path.Combine(dir, "state.ini");
        File.WriteAllText(path, text, new UTF8Encoding(false));
        return IniFile.Load(path);
    }
}
