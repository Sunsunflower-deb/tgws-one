// Record-driver shim for rust-anything (RT4).  `ServiceState.Load`/`Save` are
// file entry points over `IniFile`; the Rust port keeps only the pure
// state <-> INI conversion.  This adapter feeds the text through the real
// loader/saver in a temporary folder, so the recorded cases describe exactly
// what the original writes and reads.
// Not part of TgwsOne.slnx and not compiled into any product assembly.
using System.Text;

namespace TgwsOne.Core.Zapret;

public static class ServiceStateShims
{
    // Returns [enabled "1"/"0", strategy, scope wire, provider].
    public static string[] Load(string text)
    {
        string path = TempFile(text);
        try
        {
            ServiceState state = ServiceState.Load(path);
            return
            [
                state.Enabled ? "1" : "0",
                state.Strategy,
                state.Scope.ToWire(),
                state.Provider,
            ];
        }
        finally
        {
            File.Delete(path);
        }
    }

    // Returns the file after the real `Save`, with the platform newline
    // normalised to "\n" so cases are comparable across hosts.  A value with a
    // line break makes the original throw; the message is returned as a value
    // (`!\u001f<message>`) so the bridge compares text instead of an exception
    // type it cannot reproduce.
    public static string Save(string text, bool enabled, string strategy, string scope, string provider)
    {
        string path = TempFile(text);
        try
        {
            new ServiceState(enabled, strategy, ZapretScopes.Parse(scope), provider).Save(path);
            return File.ReadAllText(path, Encoding.UTF8).Replace("\r\n", "\n");
        }
        catch (ArgumentException error)
        {
            return "!\u001f" + error.Message;
        }
        finally
        {
            File.Delete(path);
        }
    }

    private static string TempFile(string text)
    {
        string dir = Path.Combine(Path.GetTempPath(), "rt4-state-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        string path = Path.Combine(dir, "state.ini");
        File.WriteAllText(path, text, new UTF8Encoding(false));
        return path;
    }
}
