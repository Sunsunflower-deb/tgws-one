using TgwsOne.Core;

namespace TgwsOne;

/// <summary>
/// <c>--selftest</c>: a read-only report of paths, engine, settings and proxy state, for bug reports and for
/// checking an install. Prints to the console and to <c>&lt;Logs&gt;/selftest.log</c>; the secret never appears.
/// The exit code is the number of problems found: a missing engine, or anything that threw.
/// </summary>
internal static class SelfTest
{
    public static int Run(AppPaths paths, TextWriter output)
    {
        var lines = new List<string>();
        int problems = 0;

        void Section(string name, Func<string> describe)
        {
            try { lines.Add($"{name}: {describe()}"); }
            catch (Exception ex)
            {
                problems++;
                lines.Add($"{name}: ОШИБКА — {ex.GetType().Name}: {ex.Message}");
            }
        }

        // The settings are read once without creating anything (EnsureSecret would write the INI).
        ProxySettings? settings = null;
        bool iniExists = false;
        Section("app dir", () => paths.AppDir);
        Section("engine", () =>
        {
            if (File.Exists(paths.Engine)) return $"OK {paths.Engine}";
            problems++;
            return $"MISSING {paths.Engine}";
        });
        Section("data dir", () => paths.DataDir);
        Section("ini", () =>
        {
            iniExists = File.Exists(paths.Ini);
            settings = ProxySettingsStore.Load(IniFile.Load(paths.Ini));
            string secret = ProxySettingsStore.IsValidSecret(settings.Secret) ? "ok" : "invalid";
            return $"{paths.Ini}{(iniExists ? "" : " (файла нет, значения по умолчанию)")} port={settings.Port} secret={secret}";
        });
        Section("logs", () => paths.LogsDir);
        Section("proxy", () =>
        {
            ProxyStatus st = new ProxyController(paths, new AppLog(paths.AppLogFile)).GetStatus();
            return $"running={Flag(st.Running)} listening={Flag(st.Listening)} port={st.Port}"
                   + (st.PortBusyByOther ? " (порт занят другим процессом)" : "");
        });
        if (settings is { } s)
        {
            Section("link", () => TelegramLink.Build(s.Host, s.Port, "********"));
            Section("engine argv", () => EngineCommand.Mask(EngineCommand.Build(s, paths)));
        }

        string logFile = Path.Combine(paths.LogsDir, "selftest.log");
        try
        {
            PrivateDirectory.Ensure(paths.LogsDir);
            File.WriteAllLines(logFile, lines);
        }
        catch (Exception ex)
        {
            problems++;
            lines.Add($"selftest.log: ОШИБКА — {ex.GetType().Name}: {ex.Message}");
        }

        string result = problems == 0 ? "RESULT: OK" : $"RESULT: {problems} problem(s)";
        lines.Add(result);
        foreach (string line in lines) output.WriteLine(line);
        try { File.AppendAllText(logFile, result + Environment.NewLine); } catch { /* reported on the console already */ }
        return problems;
    }

    private static string Flag(bool b) => b ? "true" : "false";
}
