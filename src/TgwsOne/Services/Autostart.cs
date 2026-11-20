using System.Runtime.Versioning;
using System.Text;

namespace TgwsOne.Services;

/// <summary>Starting with the user's session, in the tray (<c>--tray</c>: hidden, and the proxy connected).</summary>
internal interface IAutostart
{
    bool IsEnabled();

    /// <exception cref="IOException">The entry could not be written (a read-only profile, say).</exception>
    void Set(bool on);
}

/// <summary>The Windows <c>Run</c> key of the current user, as far as autostart needs it: a seam, so the writer can be tested without a registry.</summary>
internal interface IRegistryRun
{
    string? Get(string name);
    void Set(string name, string value);
    void Remove(string name);
}

internal static class Autostart
{
    public const string EntryName = "TgwsOne";

    /// <summary>What the entry runs: the executable, quoted, and the switch that starts it hidden.</summary>
    public static string CommandLine(string exe) => $"\"{exe}\" --tray";

    /// <summary>The autostart of this OS, for the executable that is running.</summary>
    public static IAutostart ForThisMachine(string exe)
        => OperatingSystem.IsWindows() ? new WindowsAutostart(new WindowsRegistryRun(), exe) : new LinuxAutostart(ConfigHome(), exe);

    private static string ConfigHome()
        => Environment.GetEnvironmentVariable("XDG_CONFIG_HOME") is { Length: > 0 } x && x.StartsWith('/')
            ? x
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".config");
}

/// <summary>Linux: <c>~/.config/autostart/tgwsone.desktop</c>, which every desktop environment reads at login.</summary>
internal sealed class LinuxAutostart(string configHome, string exe) : IAutostart
{
    private string FilePath => Path.Combine(configHome, "autostart", "tgwsone.desktop");

    public bool IsEnabled()
    {
        if (!File.Exists(FilePath)) return false;
        string text = File.ReadAllText(FilePath);
        // The file may have been put there by hand, or switched off the way desktop settings do it.
        return !text.Contains("Hidden=true", StringComparison.Ordinal)
               && !text.Contains("X-GNOME-Autostart-enabled=false", StringComparison.Ordinal);
    }

    public void Set(bool on)
    {
        if (!on)
        {
            // Nothing to remove is fine; File.Delete is quiet about a missing file but not about a missing folder.
            if (File.Exists(FilePath)) File.Delete(FilePath);
            return;
        }

        Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
        File.WriteAllText(FilePath, Text(exe), new UTF8Encoding(false));
    }

    /// <summary>The file's exact text. Exec follows the desktop-entry rules: a path with a space or a special character is double-quoted.</summary>
    public static string Text(string exe) => string.Join('\n',
        "[Desktop Entry]",
        "Type=Application",
        "Name=TgwsOne",
        "Comment=Прокси для Telegram",
        $"Exec={ExecQuoted(exe)} --tray",
        "Icon=tgwsone",
        "Terminal=false",
        "X-GNOME-Autostart-enabled=true",
        "");

    private static string ExecQuoted(string exe)
    {
        const string Special = " \t\n\"'\\><~|&;$*?#()`";
        if (!exe.Any(Special.Contains)) return exe;
        var sb = new StringBuilder("\"");
        foreach (char c in exe)
        {
            if (c is '"' or '`' or '$' or '\\') sb.Append('\\');
            sb.Append(c);
        }
        return sb.Append('"').ToString();
    }
}

/// <summary>Windows: <c>HKCU\Software\Microsoft\Windows\CurrentVersion\Run\TgwsOne</c>.</summary>
internal sealed class WindowsAutostart(IRegistryRun run, string exe) : IAutostart
{
    public bool IsEnabled() => run.Get(Autostart.EntryName) is { Length: > 0 };

    public void Set(bool on)
    {
        if (on) run.Set(Autostart.EntryName, Autostart.CommandLine(exe));
        else run.Remove(Autostart.EntryName);
    }
}

/// <summary>The real <see cref="IRegistryRun"/>. Compiled everywhere, used only on Windows (exercised there in SP5).</summary>
[SupportedOSPlatform("windows")]
internal sealed class WindowsRegistryRun : IRegistryRun
{
    private const string KeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";

    public string? Get(string name)
    {
        using Microsoft.Win32.RegistryKey? key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(KeyPath);
        return key?.GetValue(name) as string;
    }

    public void Set(string name, string value)
    {
        using Microsoft.Win32.RegistryKey key = Microsoft.Win32.Registry.CurrentUser.CreateSubKey(KeyPath);
        key.SetValue(name, value, Microsoft.Win32.RegistryValueKind.String);
    }

    public void Remove(string name)
    {
        using Microsoft.Win32.RegistryKey? key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(KeyPath, writable: true);
        key?.DeleteValue(name, throwOnMissingValue: false);
    }
}
