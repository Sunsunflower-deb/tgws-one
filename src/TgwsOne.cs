// TgwsOne — одно приложение: локальный MTProto-прокси TG WS Proxy + опциональный zapret.
// Собирается csc (C# 5), .NET Framework 4.x, WinForms. Админ не нужен для основного режима.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Net;
using System.Net.NetworkInformation;
using System.Security.Cryptography;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Windows.Forms;
using Microsoft.Win32;

namespace TgwsOne
{
    // ------------------------------------------------------------------ пути

    internal static class P
    {
        public static string AppDir { get { return AppDomain.CurrentDomain.BaseDirectory; } }
        public static string Exe { get { return Application.ExecutablePath; } }
        public static string InstalledEngine { get { return Path.Combine(AppDir, "TgWsProxy_windows.exe"); } }

        // Установлены ли мы в Program Files. Проверка по пути, а не по правам:
        // под админом Program Files «вроде бы» доступен на запись, и тогда
        // supervisor и пользовательский процесс разъезжались по разным папкам данных.
        public static bool IsProgramFiles
        {
            get
            {
                string dir = AppDir.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
                string[] roots = new string[]
                {
                    Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                    Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86)
                };
                foreach (string root in roots)
                {
                    if (string.IsNullOrEmpty(root)) continue;
                    string r = root.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
                    if (dir.StartsWith(r, StringComparison.OrdinalIgnoreCase)) return true;
                }
                return false;
            }
        }

        // Папка приложения может быть только для чтения (Program Files).
        private static int writable = -1;

        public static bool AppDirWritable
        {
            get
            {
                if (writable >= 0) return writable == 1;
                if (IsProgramFiles) { writable = 0; return false; }
                if (Environment.GetEnvironmentVariable("TGWS_READONLY") == "1") { writable = 0; return false; }
                try
                {
                    string probe = Path.Combine(AppDir, ".write-probe");
                    File.WriteAllText(probe, "ok");
                    File.Delete(probe);
                    writable = 1;
                }
                catch { writable = 0; }
                return writable == 1;
            }
        }

        // Куда пишем ini / списки / логи: папка установки, если она доступна на запись,
        // иначе %LOCALAPPDATA%\TgwsOne (обычный случай для Program Files).
        // Общесистемная папка: используется процессом под SYSTEM (задача «для всех пользователей»)
        // и при установке общесистемного обхода DPI.
        public static string MachineRoot
        {
            get
            {
                string custom = Environment.GetEnvironmentVariable("TGWS_MACHINE_ROOT");
                if (!string.IsNullOrEmpty(custom)) return custom;
                return Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
                    "TgwsOne");
            }
        }

        public static bool IsSystemUser
        {
            get
            {
                try
                {
                    return System.Security.Principal.WindowsIdentity.GetCurrent().User.Value == "S-1-5-18";
                }
                catch { return false; }
            }
        }

        public static bool MachineMode
        {
            get
            {
                if (IsSystemUser) return true;
                return Environment.GetEnvironmentVariable("TGWS_MACHINE") == "1";
            }
        }

        public static string DataRoot
        {
            get
            {
                if (MachineMode) return MachineRoot;
                if (AppDirWritable) return AppDir;
                return Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "TgwsOne");
            }
        }

        // Где реально работает движок: на месте или в копии внутри DataRoot.
        public static string EngineDir
        {
            get
            {
                if (AppDirWritable) return AppDir;
                return Path.Combine(DataRoot, "engine");
            }
        }

        public static string Engine { get { return Path.Combine(EngineDir, "TgWsProxy_windows.exe"); } }
        public static string Ini { get { return Path.Combine(DataRoot, "TgwsOne.ini"); } }
        public static string ListsDir { get { return Path.Combine(DataRoot, "lists"); } }
        public static string LogsDir { get { return Path.Combine(DataRoot, "logs"); } }
        public static string LogFile { get { return Path.Combine(LogsDir, "app.log"); } }
        public static string DataDir { get { return Path.Combine(AppDir, "TgWsProxy_data"); } }
        public static string ConfigJson { get { return Path.Combine(DataDir, "config.json"); } }
        public static string ZapretDir { get { return Path.Combine(AppDir, "zapret"); } }
        public static string Winws { get { return Path.Combine(Path.Combine(ZapretDir, "bin"), "winws.exe"); } }
        public static string StopFlag { get { return Path.Combine(LogsDir, "zapret.stop"); } }
        public static string PidFile { get { return Path.Combine(LogsDir, "zapret.pid"); } }
    }

    // ------------------------------------------------------------------ лог

    internal static class Log
    {
        public static event Action<string> Line;

        public static void Write(string text)
        {
            string line = DateTime.Now.ToString("HH:mm:ss") + "  " + text;
            try
            {
                Directory.CreateDirectory(P.LogsDir);
                FileInfo fi = new FileInfo(P.LogFile);
                if (fi.Exists && fi.Length > 1024 * 1024)
                {
                    string old = P.LogFile + ".1";
                    try { if (File.Exists(old)) File.Delete(old); } catch { }
                    try { File.Move(P.LogFile, old); } catch { }
                }
                File.AppendAllText(P.LogFile, DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + "  " + text + "\r\n", Encoding.UTF8);
            }
            catch { }
            Action<string> h = Line;
            if (h != null) { try { h(line); } catch { } }
        }

        public static void FileOnly(string text)
        {
            try
            {
                Directory.CreateDirectory(P.LogsDir);
                File.AppendAllText(P.LogFile, text + "\r\n", Encoding.UTF8);
            }
            catch { }
        }
    }

    // ------------------------------------------------------------------ настройки (INI)

    internal sealed class Settings
    {
        private readonly Dictionary<string, string> map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        private readonly List<string> order = new List<string>();
        private readonly string iniPath;

        public Settings() { }

        public Settings(string path) { iniPath = path; }

        public string IniPath { get { return iniPath ?? P.Ini; } }

        public void Load()
        {
            try { Directory.CreateDirectory(Path.GetDirectoryName(IniPath)); } catch { }
            if (!File.Exists(IniPath)) { Defaults(); Save(); return; }

            string section = "app";
            foreach (string raw in File.ReadAllLines(IniPath, Encoding.UTF8))
            {
                string line = raw.Trim();
                if (line.Length == 0 || line.StartsWith(";") || line.StartsWith("#")) continue;
                if (line.StartsWith("[") && line.EndsWith("]"))
                {
                    section = line.Substring(1, line.Length - 2).Trim().ToLowerInvariant();
                    continue;
                }
                int eq = line.IndexOf('=');
                if (eq <= 0) continue;
                string key = section + "/" + line.Substring(0, eq).Trim().ToLowerInvariant();
                SetRaw(key, line.Substring(eq + 1).Trim());
            }
            if (GetStr("proxy", "port", "") == "") { Defaults(); Save(); }
        }

        private void Defaults()
        {
            Set("proxy", "host", "127.0.0.1");
            Set("proxy", "port", "1443");
            Set("proxy", "dcmap", "2:149.154.167.220,4:149.154.167.220");
            Set("proxy", "cfproxy", "1");
            Set("proxy", "h2", "1");
            Set("proxy", "cfdomains", "");
            Set("proxy", "workerdomains", "");
            Set("proxy", "nosecure", "0");
            Set("proxy", "verbose", "1");
            Set("proxy", "bufkb", "256");
            Set("proxy", "poolsize", "4");
            Set("proxy", "logmaxmb", "5");
            Set("zapret", "strategy", "fake-tls");
            Set("zapret", "tcpports", "443");
            Set("app", "autostart", "0");
            Set("app", "secret", "");
        }

        public void Save()
        {
            List<string> lines = new List<string>();
            string current = null;
            foreach (string key in order)
            {
                int slash = key.IndexOf('/');
                string section = key.Substring(0, slash);
                if (section != current)
                {
                    if (current != null) lines.Add("");
                    lines.Add("[" + section + "]");
                    current = section;
                }
                lines.Add(key.Substring(slash + 1) + "=" + map[key]);
            }
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(IniPath));
                File.WriteAllText(IniPath, string.Join("\r\n", lines.ToArray()) + "\r\n", new UTF8Encoding(false));
            }
            catch (Exception ex) { Log.Write("не удалось сохранить настройки: " + ex.Message); }
        }

        private string Key(string s, string k) { return s.ToLowerInvariant() + "/" + k.ToLowerInvariant(); }

        private void SetRaw(string fullKey, string value)
        {
            if (!map.ContainsKey(fullKey)) order.Add(fullKey);
            map[fullKey] = value;
        }

        public void Set(string section, string key, string value) { SetRaw(Key(section, key), value); }

        public string GetStr(string section, string key, string def)
        {
            string v;
            if (map.TryGetValue(Key(section, key), out v) && !string.IsNullOrEmpty(v)) return v;
            return def;
        }

        // Возвращает значение как есть, даже пустое (нужно для полей, которые можно очистить).
        public string GetRaw(string section, string key, string def)
        {
            string v;
            if (map.TryGetValue(Key(section, key), out v)) return v;
            return def;
        }

        public int GetInt(string section, string key, int def)
        {
            int v;
            if (int.TryParse(GetStr(section, key, ""), out v)) return v;
            return def;
        }

        public bool GetBool(string section, string key, bool def)
        {
            string v = GetStr(section, key, "");
            if (v == "") return def;
            return v == "1" || v.Equals("true", StringComparison.OrdinalIgnoreCase) || v.Equals("yes", StringComparison.OrdinalIgnoreCase);
        }

        public void SetBool(string section, string key, bool value) { Set(section, key, value ? "1" : "0"); }
    }

    // ------------------------------------------------------------------ списки (зашиты в exe)

    internal static class Lists
    {
        private const string Cloudflare =
@"# Подсети Cloudflare (официальный список ips-v4 / ips-v6).
# Через них идут CF-прокси, *.workers.dev и любой домен за Cloudflare.
173.245.48.0/20
103.21.244.0/22
103.22.200.0/22
103.31.4.0/22
141.101.64.0/18
108.162.192.0/18
190.93.240.0/20
188.114.96.0/20
197.234.240.0/22
198.41.128.0/17
162.158.0.0/15
104.16.0.0/13
104.24.0.0/14
172.64.0.0/13
131.0.72.0/22
2400:cb00::/32
2606:4700::/32
2803:f800::/32
2405:b500::/32
2405:8100::/32
2a06:98c0::/29
2c0f:f248::/32
";

        private const string Telegram =
@"# Домены Telegram для TLS/WS-подключений прокси.
telegram.org
web.telegram.org
kws1.web.telegram.org
kws2.web.telegram.org
kws3.web.telegram.org
kws4.web.telegram.org
kws5.web.telegram.org
t.me
telegram.me
tdesktop.com
telesco.pe
";

        private const string ExcludeHosts =
@"# Домены, которые zapret НЕ трогает (по одному в строке).
# Пример: sberbank.ru
";

        private const string ExcludeIps =
@"# IP/подсети, которые zapret НЕ трогает.
# Пример: 203.0.113.0/24
";

        public static void EnsureAll()
        {
            try
            {
                Directory.CreateDirectory(P.ListsDir);
                Seed("cloudflare-ips.txt", Cloudflare);
                Seed("telegram.txt", Telegram);
                Seed("exclude-hosts.txt", ExcludeHosts);
                Seed("exclude-ips.txt", ExcludeIps);
            }
            catch (Exception ex) { Log.Write("списки: " + ex.Message); }
        }

        // При установке в Program Files списки по умолчанию лежат в папке установки,
        // а редактируемая копия — в DataRoot.
        private static void Seed(string name, string content)
        {
            string target = Path.Combine(P.ListsDir, name);
            if (File.Exists(target)) return;
            try
            {
                string installed = Path.Combine(Path.Combine(P.AppDir, "lists"), name);
                if (!string.Equals(P.ListsDir, Path.Combine(P.AppDir, "lists"), StringComparison.OrdinalIgnoreCase)
                    && File.Exists(installed))
                {
                    File.Copy(installed, target, false);
                    return;
                }
            }
            catch { }
            File.WriteAllText(target, content, new UTF8Encoding(false));
        }
    }

    // ------------------------------------------------------------------ прокси

    internal sealed class Proxy
    {
        private readonly Settings cfg;
        public Proxy(Settings settings) { cfg = settings; }

        public string Host { get { return cfg.GetStr("proxy", "host", "127.0.0.1"); } }
        public int Port { get { return cfg.GetInt("proxy", "port", 1443); } }
        public string Secret { get { return cfg.GetStr("app", "secret", ""); } }

        public bool EngineFound
        {
            get { return File.Exists(P.Engine) || File.Exists(P.InstalledEngine); }
        }

        // Движок работает из папки установки, если она доступна на запись,
        // иначе — из копии в %LOCALAPPDATA%\TgwsOne\engine.
        public string ActiveEngine { get { return P.Engine; } }

        private bool EnsureEngineReady()
        {
            if (string.Equals(P.EngineDir, P.AppDir, StringComparison.OrdinalIgnoreCase)) return true;
            try
            {
                FileInfo src = new FileInfo(P.InstalledEngine);
                if (!src.Exists) return false;
                Directory.CreateDirectory(P.EngineDir);
                FileInfo dst = new FileInfo(P.Engine);
                if (!dst.Exists || dst.Length != src.Length || dst.LastWriteTimeUtc != src.LastWriteTimeUtc)
                {
                    File.Copy(P.InstalledEngine, P.Engine, true);
                    Log.Write("движок скопирован в " + P.Engine);
                }
                return true;
            }
            catch (Exception ex)
            {
                Log.Write("не удалось подготовить движок: " + ex.GetType().Name + " — " + ex.Message);
                return false;
            }
        }

        public static string DataDirFor(string engine)
        {
            return Path.Combine(Path.GetDirectoryName(engine), "TgWsProxy_data");
        }

        public string ProxyLogFile
        {
            get { return Path.Combine(DataDirFor(ActiveEngine), "proxy.log"); }
        }

        public string ProxyConfigFile
        {
            get { return Path.Combine(DataDirFor(ActiveEngine), "config.json"); }
        }

        private bool IsOurs(Process p)
        {
            return PathMatches(p, P.Engine) || PathMatches(p, P.InstalledEngine);
        }

        public bool IsRunning
        {
            get
            {
                foreach (Process p in SafeProcesses("TgWsProxy_windows"))
                {
                    if (IsOurs(p)) return true;
                }
                return false;
            }
        }

        public bool IsListening
        {
            get
            {
                try
                {
                    foreach (IPEndPointInfo ep in ListenerInfo())
                    {
                        if (ep.Port == Port && ep.State == "Listen") return true;
                    }
                }
                catch { }
                return false;
            }
        }

        private sealed class IPEndPointInfo
        {
            public int Port;
            public string State;
        }

        private static List<IPEndPointInfo> ListenerInfo()
        {
            List<IPEndPointInfo> result = new List<IPEndPointInfo>();
            foreach (TcpConnectionInformation t in IPGlobalProperties.GetIPGlobalProperties().GetActiveTcpConnections())
            {
                IPEndPointInfo info = new IPEndPointInfo();
                info.Port = t.LocalEndPoint.Port;
                info.State = t.State.ToString();
                result.Add(info);
            }
            foreach (IPEndPoint ep in IPGlobalProperties.GetIPGlobalProperties().GetActiveTcpListeners())
            {
                IPEndPointInfo info = new IPEndPointInfo();
                info.Port = ep.Port;
                info.State = "Listen";
                result.Add(info);
            }
            return result;
        }

        public static Process[] SafeProcesses(string name)
        {
            try { return Process.GetProcessesByName(name); }
            catch { return new Process[0]; }
        }

        public static bool PathMatches(Process p, string exePath)
        {
            try
            {
                if (string.IsNullOrEmpty(p.MainModule.FileName)) return false;
                return string.Equals(p.MainModule.FileName, exePath, StringComparison.OrdinalIgnoreCase);
            }
            catch
            {
                // Нет прав на MainModule — не считаем процесс своим, чтобы не убить чужой экземпляр.
                return false;
            }
        }

        public string EnsureSecret()
        {
            string secret = Secret;
            if (string.IsNullOrEmpty(secret) || secret.Length != 32)
            {
                byte[] bytes = new byte[16];
                using (RandomNumberGenerator rng = RandomNumberGenerator.Create()) { rng.GetBytes(bytes); }
                StringBuilder sb = new StringBuilder();
                foreach (byte b in bytes) sb.Append(b.ToString("x2"));
                secret = sb.ToString();
                cfg.Set("app", "secret", secret);
                cfg.Save();
                Log.Write("создан новый secret (сохранён в TgwsOne.ini)");
            }
            return secret;
        }

        // ---- конфиг прокси (portable, рядом с exe движка)

        public void WriteConfig()
        {
            string secret = EnsureSecret();
            List<string> kv = new List<string>();

            kv.Add("host: " + Q(Host));
            kv.Add("port: " + Port.ToString());
            kv.Add("secret: " + Q(secret));
            kv.Add("dc_ip: " + ArrayOf(cfg.GetRaw("proxy", "dcmap", "2:149.154.167.220,4:149.154.167.220")));
            kv.Add("verbose: " + B(cfg.GetBool("proxy", "verbose", true)));
            kv.Add("check_updates: false");
            kv.Add("log_max_mb: " + cfg.GetInt("proxy", "logmaxmb", 5));
            kv.Add("buf_kb: " + cfg.GetInt("proxy", "bufkb", 256));
            kv.Add("pool_size: " + cfg.GetInt("proxy", "poolsize", 4));
            kv.Add("cfproxy: " + B(cfg.GetBool("proxy", "cfproxy", true)));
            kv.Add("h2: " + B(cfg.GetBool("proxy", "h2", true)));

            string cf = cfg.GetStr("proxy", "cfdomains", "");
            string workers = cfg.GetStr("proxy", "workerdomains", "");
            kv.Add("cfproxy_user_domain_enabled: " + B(Split(cf).Count > 0));
            kv.Add("cfproxy_user_domain: " + ArrayOf(cf));
            kv.Add("cfproxy_worker_enabled: " + B(Split(workers).Count > 0));
            kv.Add("cfproxy_worker_domain: " + ArrayOf(workers));

            kv.Add("force_test_dc: false");
            kv.Add("no_secure: " + B(cfg.GetBool("proxy", "nosecure", false)));
            kv.Add("appearance: \"auto\"");
            kv.Add("language: \"ru\"");

            StringBuilder sb = new StringBuilder();
            sb.Append("{\r\n");
            for (int i = 0; i < kv.Count; i++)
            {
                sb.Append("  \"" + kv[i].Substring(0, kv[i].IndexOf(':')) + "\": " + kv[i].Substring(kv[i].IndexOf(':') + 2));
                if (i < kv.Count - 1) sb.Append(',');
                sb.Append("\r\n");
            }
            sb.Append("}\r\n");

            string dataDir = DataDirFor(ActiveEngine);
            Directory.CreateDirectory(dataDir);
            File.WriteAllText(Path.Combine(dataDir, "config.json"), sb.ToString(), new UTF8Encoding(false));
        }

        private static string Q(string s)
        {
            return "\"" + s.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"";
        }

        private static string B(bool v) { return v ? "true" : "false"; }

        private static List<string> Split(string value)
        {
            List<string> result = new List<string>();
            if (string.IsNullOrEmpty(value)) return result;
            foreach (string part in value.Split(new char[] { ',', ';', ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries))
            {
                string item = part.Trim();
                if (item.Length > 0 && !result.Contains(item)) result.Add(item);
            }
            return result;
        }

        private static string ArrayOf(string value)
        {
            List<string> items = Split(value);
            List<string> quoted = new List<string>();
            foreach (string i in items) quoted.Add(Q(i));
            return "[" + string.Join(", ", quoted.ToArray()) + "]";
        }

        // ---- запуск / остановка

        // Прокси не умеет работать в два экземпляра: второй показывает диалог и висит.
        public int ForeignInstances
        {
            get
            {
                int count = 0;
                foreach (Process p in SafeProcesses("TgWsProxy_windows"))
                {
                    if (!IsOurs(p)) count++;
                }
                return count;
            }
        }

        public bool PortBusy
        {
            get { return IsListening && !IsRunning; }
        }

        public bool Start()
        {
            if (!EngineFound)
            {
                Log.Write("движок не найден: " + P.Engine);
                return false;
            }
            if (IsRunning)
            {
                Log.Write("прокси уже запущен");
                return true;
            }

            if (PortBusy)
            {
                Log.Write("порт " + Port + " уже занят другим приложением — закройте его или смените port в TgwsOne.ini");
                return false;
            }

            if (ForeignInstances > 0)
            {
                Log.Write("уже запущен другой экземпляр TgWsProxy_windows.exe — прокси не умеет работать вдвоём, закройте его");
                return false;
            }

            if (!EnsureEngineReady())
            {
                Log.Write("движок недоступен: " + P.InstalledEngine);
                return false;
            }

            string engine = P.Engine;
            WriteConfig();

            if (LaunchAndWait(engine, 15)) return true;

            Log.Write("прокси не поднялся: порт " + Port + " не слушается. Смотрите proxy.log");
            return false;
        }

        private bool LaunchAndWait(string engine, int seconds)
        {
            try
            {
                Log.Write("запускаю движок: " + engine);
                ProcessStartInfo psi = new ProcessStartInfo(engine);
                psi.WorkingDirectory = Path.GetDirectoryName(engine);
                psi.UseShellExecute = false;
                psi.CreateNoWindow = false;
                Process started = Process.Start(psi);
                Log.Write("движок запущен, PID " + (started == null ? 0 : started.Id));
            }
            catch (Exception ex)
            {
                Log.Write("не удалось запустить прокси: " + ex.Message);
                return false;
            }

            for (int i = 0; i < seconds * 4; i++)
            {
                Thread.Sleep(250);
                if (IsListening)
                {
                    Log.Write("прокси слушает " + Host + ":" + Port);
                    return true;
                }
            }
            return false;
        }

        public void Stop()
        {
            bool any = false;
            foreach (Process p in SafeProcesses("TgWsProxy_windows"))
            {
                if (!IsOurs(p)) continue;
                try { p.Kill(); p.WaitForExit(3000); any = true; }
                catch (Exception ex) { Log.Write("не удалось остановить прокси: " + ex.Message); }
            }
            if (any) Log.Write("прокси остановлен");
            else Log.Write("прокси не был запущен");
        }

        public string TelegramLink()
        {
            string host = Host == "0.0.0.0" ? "127.0.0.1" : Host;
            return "tg://proxy?server=" + host + "&port=" + Port + "&secret=dd" + EnsureSecret();
        }

        public void OpenInTelegram()
        {
            string link = TelegramLink();
            ThreadPool.QueueUserWorkItem(delegate
            {
                try { Process.Start(link); Log.Write("открываю ссылку в Telegram"); }
                catch (Exception ex) { Log.Write("не удалось открыть tg:// — " + ex.Message + " · ссылка: " + link); }
            });
        }
    }

    // ------------------------------------------------------------------ zapret (опционально)

    internal sealed class Zapret
    {
        public const string TaskName = "TgwsOne-Zapret";
        private readonly Settings cfg;
        public Zapret(Settings settings) { cfg = settings; }

        public bool BundleFound { get { return File.Exists(P.Winws); } }

        private static bool taskInstalled;
        private static DateTime taskCheckedAt = DateTime.MinValue;
        private static int taskCheckRunning;

        // Актуальный опрос (блокирует) — только для действий пользователя.
        public bool QueryTask()
        {
            int code = Run("schtasks.exe", "/query /tn \"" + TaskName + "\"", true);
            taskInstalled = code == 0;
            taskCheckedAt = DateTime.UtcNow;
            return taskInstalled;
        }

        // Кэш: UI не должен запускать schtasks каждые полтора секунды.
        public bool TaskInstalled
        {
            get { return taskInstalled; }
        }

        public void RefreshTaskStateAsync()
        {
            if (DateTime.UtcNow - taskCheckedAt < TimeSpan.FromSeconds(10)) return;
            if (System.Threading.Interlocked.CompareExchange(ref taskCheckRunning, 1, 0) != 0) return;
            ThreadPool.QueueUserWorkItem(delegate
            {
                try { QueryTask(); }
                catch { }
                finally { System.Threading.Interlocked.Exchange(ref taskCheckRunning, 0); }
            });
        }

        public bool WinwsRunning
        {
            get { foreach (Process p in Proxy.SafeProcesses("winws")) { return true; } return false; }
        }

        public bool SupervisorRunning
        {
            get
            {
                try
                {
                    if (!File.Exists(P.PidFile)) return false;
                    int pid = int.Parse(File.ReadAllText(P.PidFile).Trim());
                    Process p = Process.GetProcessById(pid);
                    return !p.HasExited;
                }
                catch { return false; }
            }
        }

        public string Strategy { get { return cfg.GetStr("zapret", "strategy", "fake-tls"); } }

        public List<string> BuildArgs()
        {
            string bin = Path.GetDirectoryName(P.Winws);
            string lists = P.ListsDir;
            string ports = cfg.GetStr("zapret", "tcpports", "443");
            string strategy = Strategy;

            List<string> desync = new List<string>();
            if (strategy == "split")
            {
                desync.Add("--dpi-desync=multisplit");
                desync.Add("--dpi-desync-split-seqovl=681");
                desync.Add("--dpi-desync-split-pos=1");
                desync.Add("--dpi-desync-split-seqovl-pattern=" + Path.Combine(bin, "tls_clienthello_www_google_com.bin"));
            }
            else if (strategy == "split-stealth")
            {
                desync.Add("--dpi-desync=multisplit");
                desync.Add("--dpi-desync-split-seqovl=568");
                desync.Add("--dpi-desync-split-pos=1");
                desync.Add("--dpi-desync-split-seqovl-pattern=" + Path.Combine(bin, "tls_clienthello_4pda_to.bin"));
            }
            else if (strategy == "aggressive")
            {
                desync.Add("--dpi-desync=fake,split2");
                desync.Add("--dpi-desync-split-pos=1");
                desync.Add("--dpi-desync-repeats=11");
                desync.Add("--dpi-desync-fooling=md5sig");
                desync.Add("--dpi-desync-fake-tls=" + Path.Combine(bin, "tls_clienthello_www_google_com.bin"));
            }
            else
            {
                desync.Add("--dpi-desync=fake,multidisorder");
                desync.Add("--dpi-desync-split-pos=1,midsld");
                desync.Add("--dpi-desync-repeats=11");
                desync.Add("--dpi-desync-fooling=badseq");
                desync.Add("--dpi-desync-fake-tls=0x00000000");
                desync.Add("--dpi-desync-fake-tls=!");
                desync.Add("--dpi-desync-fake-tls-mod=rnd,dupsid,sni=www.google.com");
            }

            string cfSet = Path.Combine(lists, "cloudflare-ips.txt");
            string tgHosts = Path.Combine(lists, "telegram.txt");
            string exHosts = Path.Combine(lists, "exclude-hosts.txt");
            string exIps = Path.Combine(lists, "exclude-ips.txt");

            List<string> args = new List<string>();
            args.Add("--wf-tcp=" + ports);

            args.Add("--filter-tcp=" + ports);
            args.Add("--ipset=" + cfSet);
            if (HasEntries(exIps)) args.Add("--ipset-exclude=" + exIps);
            if (HasEntries(exHosts)) args.Add("--hostlist-exclude=" + exHosts);
            args.AddRange(desync);
            args.Add("--new");

            args.Add("--filter-tcp=" + ports);
            args.Add("--hostlist=" + tgHosts);
            if (HasEntries(exHosts)) args.Add("--hostlist-exclude=" + exHosts);
            args.AddRange(desync);

            return args;
        }

        private static bool HasEntries(string path)
        {
            try
            {
                if (!File.Exists(path)) return false;
                foreach (string line in File.ReadAllLines(path))
                {
                    string t = line.Trim();
                    if (t.Length > 0 && !t.StartsWith("#")) return true;
                }
            }
            catch { }
            return false;
        }

        public string CommandLine()
        {
            StringBuilder sb = new StringBuilder();
            foreach (string a in BuildArgs())
            {
                if (sb.Length > 0) sb.Append(' ');
                sb.Append(a.IndexOf(' ') >= 0 ? "\"" + a + "\"" : a);
            }
            return sb.ToString();
        }

        // ---- режим супервизора (запускается задачей планировщика, с правами админа)

        public int RunSupervisor()
        {
            List<string> args = BuildArgs();
            Log.Write("zapret: запускаю winws (" + Strategy + ")");

            try { if (File.Exists(P.StopFlag)) File.Delete(P.StopFlag); } catch { }

            ProcessStartInfo psi = new ProcessStartInfo(P.Winws);
            psi.Arguments = CommandLine();
            psi.WorkingDirectory = Path.GetDirectoryName(P.Winws);
            psi.UseShellExecute = false;
            psi.CreateNoWindow = true;
            psi.WindowStyle = ProcessWindowStyle.Hidden;

            string winwsLog = Path.Combine(P.LogsDir, "winws.log");
            StreamWriter wlog = null;
            try { wlog = new StreamWriter(winwsLog, false, Encoding.UTF8); wlog.AutoFlush = true; } catch { }
            psi.RedirectStandardOutput = true;
            psi.RedirectStandardError = true;

            Process proc;
            try { proc = Process.Start(psi); }
            catch (Exception ex) { Log.Write("zapret: winws не запустился — " + ex.Message); return 3; }

            if (wlog != null)
            {
                StreamWriter captured = wlog;
                DataReceivedEventHandler sink = delegate(object s, DataReceivedEventArgs e)
                {
                    if (e.Data == null) return;
                    try { lock (captured) { captured.WriteLine(e.Data); } } catch { }
                };
                proc.OutputDataReceived += sink;
                proc.ErrorDataReceived += sink;
                try { proc.BeginOutputReadLine(); proc.BeginErrorReadLine(); } catch { }
                Log.Write("zapret: вывод winws пишется в " + winwsLog);
            }

            try { File.WriteAllText(P.PidFile, proc.Id.ToString()); } catch { }
            Log.Write("zapret: winws PID " + proc.Id);

            while (!proc.HasExited)
            {
                if (File.Exists(P.StopFlag))
                {
                    try { proc.Kill(); } catch { }
                    break;
                }
                Thread.Sleep(500);
            }

            try { File.Delete(P.PidFile); } catch { }
            try { File.Delete(P.StopFlag); } catch { }
            Log.Write("zapret: остановлен");
            return 0;
        }

        // ---- установка / управление (требует прав админа только здесь)

        // Вызывается MSI-установщиком (идёт с правами админа): создаёт задачу автозапуска
        // для текущего пользователя, чтобы потом не требовался UAC.
        // Задача описывается XML — так не надо экранировать кавычки в путях с пробелами.
        public static string BuildTaskXml()
        {
            return BuildTaskXml(false);
        }

        public static string BuildTaskXml(bool allUsers)
        {
            string user = Environment.UserDomainName + "\\" + Environment.UserName;
            StringBuilder sb = new StringBuilder();
            sb.AppendLine("<?xml version=\"1.0\" encoding=\"UTF-16\"?>");
            sb.AppendLine("<Task version=\"1.2\" xmlns=\"http://schemas.microsoft.com/windows/2004/02/mit/task\">");
            sb.AppendLine("  <RegistrationInfo>");
            sb.AppendLine("    <Description>TG WS Proxy: обход DPI (zapret) " +
                (allUsers ? "для всех пользователей" : "при входе в систему") + "</Description>");
            sb.AppendLine("  </RegistrationInfo>");
            sb.AppendLine("  <Triggers>");
            if (allUsers)
            {
                sb.AppendLine("    <BootTrigger>");
                sb.AppendLine("      <Enabled>true</Enabled>");
                sb.AppendLine("      <Delay>PT30S</Delay>");
                sb.AppendLine("    </BootTrigger>");
            }
            else
            {
                sb.AppendLine("    <LogonTrigger>");
                sb.AppendLine("      <Enabled>true</Enabled>");
                sb.AppendLine("    </LogonTrigger>");
            }
            sb.AppendLine("  </Triggers>");
            sb.AppendLine("  <Principals>");
            sb.AppendLine("    <Principal id=\"Author\">");
            sb.AppendLine("      <UserId>" + (allUsers ? "S-1-5-18" : X(user)) + "</UserId>");
            sb.AppendLine("      <LogonType>" + (allUsers ? "ServiceAccount" : "InteractiveToken") + "</LogonType>");
            sb.AppendLine("      <RunLevel>HighestAvailable</RunLevel>");
            sb.AppendLine("    </Principal>");
            sb.AppendLine("  </Principals>");
            sb.AppendLine("  <Settings>");
            sb.AppendLine("    <MultipleInstancesPolicy>IgnoreNew</MultipleInstancesPolicy>");
            sb.AppendLine("    <DisallowStartIfOnBatteries>false</DisallowStartIfOnBatteries>");
            sb.AppendLine("    <StopIfGoingOnBatteries>false</StopIfGoingOnBatteries>");
            sb.AppendLine("    <AllowHardTerminate>true</AllowHardTerminate>");
            sb.AppendLine("    <StartWhenAvailable>true</StartWhenAvailable>");
            sb.AppendLine("    <RunOnlyIfNetworkAvailable>false</RunOnlyIfNetworkAvailable>");
            sb.AppendLine("    <IdleSettings>");
            sb.AppendLine("      <StopOnIdleEnd>false</StopOnIdleEnd>");
            sb.AppendLine("      <RestartOnIdle>false</RestartOnIdle>");
            sb.AppendLine("    </IdleSettings>");
            sb.AppendLine("    <AllowStartOnDemand>true</AllowStartOnDemand>");
            sb.AppendLine("    <Enabled>true</Enabled>");
            sb.AppendLine("    <Hidden>false</Hidden>");
            sb.AppendLine("    <RunOnlyIfIdle>false</RunOnlyIfIdle>");
            sb.AppendLine("    <WakeToRun>false</WakeToRun>");
            sb.AppendLine("    <ExecutionTimeLimit>PT0S</ExecutionTimeLimit>");
            sb.AppendLine("    <Priority>7</Priority>");
            sb.AppendLine("  </Settings>");
            sb.AppendLine("  <Actions Context=\"Author\">");
            sb.AppendLine("    <Exec>");
            sb.AppendLine("      <Command>" + X(P.Exe) + "</Command>");
            sb.AppendLine("      <Arguments>--zapret-start</Arguments>");
            sb.AppendLine("    </Exec>");
            sb.AppendLine("  </Actions>");
            sb.AppendLine("</Task>");
            return sb.ToString();
        }

        private static string X(string s)
        {
            return s.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;");
        }

        public static bool EnsureTask()
        {
            try
            {
                string xmlPath = Path.Combine(P.LogsDir, "task.xml");
                Directory.CreateDirectory(P.LogsDir);
                File.WriteAllText(xmlPath, BuildTaskXml(), Encoding.Unicode);
                int code = Run("schtasks.exe",
                    "/create /f /tn \"" + TaskName + "\" /xml \"" + xmlPath + "\"", true);
                try { File.Delete(xmlPath); } catch { }
                return code == 0;
            }
            catch (Exception ex)
            {
                Log.Write("создание задачи: " + ex.Message);
                return false;
            }
        }

        public static bool RemoveTask()
        {
            Run("schtasks.exe", "/end /tn \"" + TaskName + "\"", true);
            int code = Run("schtasks.exe", "/delete /f /tn \"" + TaskName + "\"", true);
            return code == 0;
        }

        // ---------------- режим «для всех пользователей» ----------------

        public const string MachineFlag = "all-users.flag";

        public string MachineIni { get { return Path.Combine(P.MachineRoot, "TgwsOne.ini"); } }

        public bool MachineInstalled
        {
            get
            {
                try { return File.Exists(Path.Combine(P.MachineRoot, MachineFlag)); }
                catch { return false; }
            }
        }

        public static bool IsAdmin()
        {
            try
            {
                System.Security.Principal.WindowsIdentity id = System.Security.Principal.WindowsIdentity.GetCurrent();
                return new System.Security.Principal.WindowsPrincipal(id)
                    .IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator);
            }
            catch { return false; }
        }

        // Списки и настройки кладём в ProgramData, задачу создаём от SYSTEM при старте системы.
        public bool EnsureMachineTask()
        {
            try
            {
                Directory.CreateDirectory(P.MachineRoot);
                string machineLists = Path.Combine(P.MachineRoot, "lists");
                Directory.CreateDirectory(machineLists);

                foreach (string name in new string[] { "cloudflare-ips.txt", "telegram.txt", "exclude-hosts.txt", "exclude-ips.txt" })
                {
                    string dst = Path.Combine(machineLists, name);
                    if (File.Exists(dst)) continue;
                    try
                    {
                        // при установке списков пользователя ещё нет — берём из папки установки
                        string fromInstall = Path.Combine(Path.Combine(P.AppDir, "lists"), name);
                        string fromUser = Path.Combine(P.ListsDir, name);
                        string src = File.Exists(fromInstall) ? fromInstall : fromUser;
                        if (File.Exists(src)) File.Copy(src, dst, false);
                    }
                    catch { }
                }

                if (!File.Exists(MachineIni))
                {
                    string text = "[zapret]" + "\r\n" +
                                  "strategy=" + Strategy + "\r\n" +
                                  "tcpports=" + cfg.GetStr("zapret", "tcpports", "443") + "\r\n";
                    File.WriteAllText(MachineIni, text, new UTF8Encoding(false));
                }

                File.WriteAllText(Path.Combine(P.MachineRoot, MachineFlag), "1", new UTF8Encoding(false));

                RemoveTask(); // старая задача «только для этого пользователя» больше не нужна

                string xmlPath = Path.Combine(P.MachineRoot, "task.xml");
                File.WriteAllText(xmlPath, BuildTaskXml(true), Encoding.Unicode);
                int code = Run("schtasks.exe", "/create /f /tn \"" + TaskName + "\" /xml \"" + xmlPath + "\"", true);
                try { File.Delete(xmlPath); } catch { }
                if (code != 0)
                {
                    Log.Write("zapret(все пользователи): задача не создана, код " + code);
                    return false;
                }

                Run("schtasks.exe", "/run /tn \"" + TaskName + "\"", true);
                return true;
            }
            catch (Exception ex)
            {
                Log.Write("zapret(все пользователи): " + ex.Message);
                return false;
            }
        }

        public bool RemoveMachineTask()
        {
            Run("schtasks.exe", "/end /tn \"" + TaskName + "\"", true);
            Run("schtasks.exe", "/delete /f /tn \"" + TaskName + "\"", true);
            try { File.Delete(Path.Combine(P.MachineRoot, MachineFlag)); } catch { }
            return true;
        }

        public void SetMachineEnabled(bool enable)
        {
            if (enable)
            {
                Run("schtasks.exe", "/run /tn \"" + TaskName + "\"", true);
                return;
            }
            Run("schtasks.exe", "/end /tn \"" + TaskName + "\"", true);
            foreach (Process p in Proxy.SafeProcesses("winws"))
            {
                try { p.Kill(); } catch { }
            }
        }

        // Смена стратегии в общесистемной конфигурации (нужны права администратора).
        public bool CycleMachineStrategy()
        {
            try
            {
                Settings machine = new Settings(MachineIni);
                machine.Load();
                string[] order = new string[] { "fake-tls", "split", "split-stealth", "aggressive" };
                string current = machine.GetStr("zapret", "strategy", "fake-tls");
                int index = Array.IndexOf(order, current);
                string next = order[(index + 1) % order.Length];
                machine.Set("zapret", "strategy", next);
                machine.Save();
                Log.Write("стратегия для всех пользователей: " + current + " -> " + next);
                SetMachineEnabled(false);
                System.Threading.Thread.Sleep(500);
                SetMachineEnabled(true);
                return true;
            }
            catch (Exception ex)
            {
                Log.Write("смена стратегии (все пользователи): " + ex.Message);
                return false;
            }
        }

        public bool Install()
        {
            if (!BundleFound) { Log.Write("zapret: не найден " + P.Winws); return false; }

            if (!EnsureTask())
            {
                Log.Write("zapret: не удалось создать задачу");
                return false;
            }
            Log.Write("zapret: автозапуск установлен (задача " + TaskName + ")");
            StartViaTask();
            return true;
        }

        public bool Uninstall()
        {
            RequestStop();
            Run("schtasks.exe", "/end /tn \"" + TaskName + "\"", true);
            int code = Run("schtasks.exe", "/delete /f /tn \"" + TaskName + "\"", true);
            if (code == 0) Log.Write("zapret: задача удалена");
            return code == 0;
        }

        public bool StartViaTask()
        {
            if (!TaskInstalled) return false;
            int code = Run("schtasks.exe", "/run /tn \"" + TaskName + "\"", true);
            if (code != 0) { Log.Write("zapret: не удалось запустить задачу (код " + code + ")"); return false; }
            for (int i = 0; i < 30; i++)
            {
                Thread.Sleep(300);
                if (WinwsRunning) { Log.Write("zapret: работает"); return true; }
            }
            Log.Write("zapret: задача запущена, но winws не появился");
            return false;
        }

        public void RequestStop()
        {
            try
            {
                Directory.CreateDirectory(P.LogsDir);
                File.WriteAllText(P.StopFlag, "stop");
            }
            catch { }
            for (int i = 0; i < 20; i++)
            {
                Thread.Sleep(250);
                if (!WinwsRunning) break;
            }
            if (WinwsRunning) Run("schtasks.exe", "/end /tn \"" + TaskName + "\"", true);
            Log.Write("zapret: остановлен");
        }

        // Запуск себя же с повышением прав: единственное место, где вообще появляется UAC.
        public static int RunElevated(string args)
        {
            try
            {
                ProcessStartInfo psi = new ProcessStartInfo(P.Exe, args);
                psi.UseShellExecute = true;
                psi.Verb = "runas";
                using (Process p = Process.Start(psi))
                {
                    if (!p.WaitForExit(90000)) return -2;
                    return p.ExitCode;
                }
            }
            catch (Exception ex)
            {
                Log.Write("UAC отменён или недоступен: " + ex.Message);
                return -1;
            }
        }

        public static int Run(string file, string args, bool quiet)
        {
            try
            {
                ProcessStartInfo psi = new ProcessStartInfo(file, args);
                psi.UseShellExecute = false;
                psi.CreateNoWindow = true;
                if (quiet) { psi.RedirectStandardOutput = true; psi.RedirectStandardError = true; }
                using (Process p = Process.Start(psi))
                {
                    if (quiet) { p.StandardOutput.ReadToEnd(); p.StandardError.ReadToEnd(); }
                    p.WaitForExit(20000);
                    return p.HasExited ? p.ExitCode : -1;
                }
            }
            catch (Exception ex)
            {
                Log.Write("команда " + file + " " + args + ": " + ex.Message);
                return -1;
            }
        }
    }

    // ------------------------------------------------------------------ главное окно

    // ------------------------------------------------------------------ диалог настроек

    internal sealed class SettingsForm : Form
    {
        private readonly Settings cfg;
        private readonly Proxy proxy;

        private TextBox txtHost;
        private TextBox txtPort;
        private TextBox txtDc;
        private TextBox txtCf;
        private TextBox txtWorker;
        private TextBox txtPorts;
        private TextBox txtBuf;
        private TextBox txtPool;
        private TextBox txtLog;
        private TextBox txtSecret;
        private TextBox txtLink;
        private CheckBox chkCf;
        private CheckBox chkH2;
        private CheckBox chkNoSecure;
        private ComboBox cbStrategy;

        public bool StrategyChanged;
        public bool ProxySettingsChanged;

        public SettingsForm(Settings settings, Proxy p)
        {
            cfg = settings;
            proxy = p;

            Text = "Настройки — TG WS Proxy";
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            MinimizeBox = false;
            ShowInTaskbar = false;
            StartPosition = FormStartPosition.CenterParent;
            ClientSize = new Size(600, 672);
            Font = new Font("Segoe UI", 9f);

            BuildUi();
            LoadValues();
        }

        // ---------- построение ----------

        private GroupBox Group(string title, int y, int height)
        {
            GroupBox g = new GroupBox();
            g.Text = title;
            g.Location = new Point(12, y);
            g.Size = new Size(576, height);
            Controls.Add(g);
            return g;
        }

        private static Label Caption(Control parent, string text, int x, int y)
        {
            Label l = new Label();
            l.Text = text;
            l.AutoSize = true;
            l.Location = new Point(x, y);
            parent.Controls.Add(l);
            return l;
        }

        private static TextBox Box(Control parent, int x, int y, int w, int h, string text)
        {
            TextBox t = new TextBox();
            t.Location = new Point(x, y);
            t.Size = new Size(w, h);
            t.Text = text;
            parent.Controls.Add(t);
            return t;
        }

        private static CheckBox Check(Control parent, string text, int x, int y, int w)
        {
            CheckBox c = new CheckBox();
            c.Text = text;
            c.AutoSize = false;
            c.Location = new Point(x, y);
            c.Size = new Size(w, 22);
            parent.Controls.Add(c);
            return c;
        }

        private void BuildUi()
        {
            GroupBox gProxy = Group("Прокси", 8, 250);

            Caption(gProxy, "Хост", 12, 26);
            txtHost = Box(gProxy, 60, 22, 120, 24, "");
            Caption(gProxy, "Порт", 196, 26);
            txtPort = Box(gProxy, 240, 22, 70, 24, "");

            Caption(gProxy, "DC → IP (по строке, формат номер:IP)", 12, 54);
            txtDc = Box(gProxy, 12, 74, 548, 68, "");
            txtDc.Multiline = true;
            txtDc.ScrollBars = ScrollBars.Vertical;
            txtDc.Font = new Font("Consolas", 9f);

            chkCf = Check(gProxy, "CF-прокси", 12, 152, 100);
            chkH2 = Check(gProxy, "Мультиплексирование (HTTP/2)", 118, 152, 252);
            chkNoSecure = Check(gProxy, "Без TLS (порт 80)", 378, 152, 180);

            Caption(gProxy, "Свои CF-домены", 12, 182);
            txtCf = Box(gProxy, 140, 178, 420, 24, "");
            Caption(gProxy, "Worker-домены", 12, 212);
            txtWorker = Box(gProxy, 140, 208, 420, 24, "");

            GroupBox gZap = Group("Обход DPI (zapret)", 266, 104);

            Caption(gZap, "Стратегия", 12, 26);
            cbStrategy = new ComboBox();
            cbStrategy.DropDownStyle = ComboBoxStyle.DropDownList;
            cbStrategy.Location = new Point(90, 22);
            cbStrategy.Size = new Size(190, 24);
            cbStrategy.Items.AddRange(new object[] { "fake-tls", "split", "split-stealth", "aggressive" });
            gZap.Controls.Add(cbStrategy);

            Caption(gZap, "Порты", 286, 26);
            txtPorts = Box(gZap, 336, 22, 80, 24, "");

            Label hint = new Label();
            hint.Text = "После смены стратегии обход DPI перезапускается автоматически.";
            hint.ForeColor = Color.DimGray;
            hint.AutoSize = true;
            hint.Location = new Point(12, 60);
            gZap.Controls.Add(hint);

            GroupBox gPerf = Group("Производительность", 378, 84);

            Caption(gPerf, "Буфер, КБ", 12, 30);
            txtBuf = Box(gPerf, 86, 26, 70, 24, "");
            Caption(gPerf, "Пул WS", 176, 30);
            txtPool = Box(gPerf, 236, 26, 60, 24, "");
            Caption(gPerf, "Макс. лог, МБ", 320, 30);
            txtLog = Box(gPerf, 424, 26, 60, 24, "");

            GroupBox gSec = Group("Секрет и ссылка", 470, 126);

            Caption(gSec, "Секрет", 12, 22);
            txtSecret = Box(gSec, 12, 42, 400, 24, "");
            txtSecret.ReadOnly = true;
            txtSecret.Font = new Font("Consolas", 9f);

            Button btnNew = new Button();
            btnNew.Text = "Новый…";
            btnNew.Location = new Point(424, 40);
            btnNew.Size = new Size(124, 26);
            btnNew.FlatStyle = FlatStyle.System;
            btnNew.Click += delegate { RegenerateSecret(); };
            gSec.Controls.Add(btnNew);

            Caption(gSec, "Ссылка", 12, 76);
            txtLink = Box(gSec, 12, 96, 400, 24, "");
            txtLink.ReadOnly = true;
            txtLink.Font = new Font("Consolas", 8.25f);

            Button btnCopy = new Button();
            btnCopy.Text = "Копировать";
            btnCopy.Location = new Point(424, 94);
            btnCopy.Size = new Size(124, 26);
            btnCopy.FlatStyle = FlatStyle.System;
            btnCopy.Click += delegate
            {
                try { Clipboard.SetText(txtLink.Text); Log.Write("ссылка скопирована"); }
                catch (Exception ex) { Log.Write("буфер обмена: " + ex.Message); }
            };
            gSec.Controls.Add(btnCopy);

            Button btnCancel = new Button();
            btnCancel.Text = "Отмена";
            btnCancel.Location = new Point(356, 626);
            btnCancel.Size = new Size(110, 32);
            btnCancel.FlatStyle = FlatStyle.System;
            btnCancel.DialogResult = DialogResult.Cancel;
            Controls.Add(btnCancel);

            Button btnSave = new Button();
            btnSave.Text = "Сохранить";
            btnSave.Location = new Point(476, 626);
            btnSave.Size = new Size(112, 32);
            btnSave.FlatStyle = FlatStyle.System;
            btnSave.Click += delegate { SaveAndClose(); };
            Controls.Add(btnSave);

            AcceptButton = btnSave;
            CancelButton = btnCancel;
        }

        // ---------- данные ----------

        private void LoadValues()
        {
            txtHost.Text = cfg.GetStr("proxy", "host", "127.0.0.1");
            txtPort.Text = cfg.GetInt("proxy", "port", 1443).ToString();
            txtDc.Text = cfg.GetRaw("proxy", "dcmap", "2:149.154.167.220,4:149.154.167.220").Replace(",", "\r\n");
            chkCf.Checked = cfg.GetBool("proxy", "cfproxy", true);
            chkH2.Checked = cfg.GetBool("proxy", "h2", true);
            chkNoSecure.Checked = cfg.GetBool("proxy", "nosecure", false);
            txtCf.Text = cfg.GetStr("proxy", "cfdomains", "");
            txtWorker.Text = cfg.GetStr("proxy", "workerdomains", "");
            txtPorts.Text = cfg.GetStr("zapret", "tcpports", "443");
            txtBuf.Text = cfg.GetInt("proxy", "bufkb", 256).ToString();
            txtPool.Text = cfg.GetInt("proxy", "poolsize", 4).ToString();
            txtLog.Text = cfg.GetInt("proxy", "logmaxmb", 5).ToString();

            string strategy = cfg.GetStr("zapret", "strategy", "fake-tls");
            cbStrategy.SelectedItem = strategy;
            if (cbStrategy.SelectedIndex < 0) cbStrategy.SelectedIndex = 0;

            txtSecret.Text = proxy.EnsureSecret();
            UpdateLink();

            txtPort.TextChanged += delegate { UpdateLink(); };
            txtHost.TextChanged += delegate { UpdateLink(); };
        }

        private void UpdateLink()
        {
            string host = txtHost.Text.Trim();
            if (host == "0.0.0.0" || host.Length == 0) host = "127.0.0.1";
            int port = 1443;
            int.TryParse(txtPort.Text.Trim(), out port);
            txtLink.Text = "tg://proxy?server=" + host + "&port=" + port + "&secret=dd" + txtSecret.Text.Trim();
        }

        private void RegenerateSecret()
        {
            if (MessageBox.Show(this,
                    "Новый секрет заменит текущий: старая запись прокси в Telegram перестанет работать,\n" +
                    "нужно будет снова нажать «Открыть в Telegram».\n\nПродолжить?",
                    "Новый секрет", MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return;

            byte[] bytes = new byte[16];
            using (System.Security.Cryptography.RandomNumberGenerator rng =
                   System.Security.Cryptography.RandomNumberGenerator.Create())
            {
                rng.GetBytes(bytes);
            }
            StringBuilder sb = new StringBuilder();
            foreach (byte b in bytes) sb.Append(b.ToString("x2"));
            txtSecret.Text = sb.ToString();
            cfg.Set("app", "secret", txtSecret.Text);
            cfg.Save();
            UpdateLink();
            Log.Write("секрет перегенерирован");
        }

        private void SaveAndClose()
        {
            int port;
            if (!int.TryParse(txtPort.Text.Trim(), out port) || port < 1 || port > 65535)
            {
                MessageBox.Show(this, "Порт должен быть числом от 1 до 65535.", "Настройки",
                    MessageBoxButtons.OK, MessageBoxIcon.Warning);
                txtPort.Focus();
                return;
            }

            string host = txtHost.Text.Trim();
            System.Net.IPAddress parsed;
            if (!System.Net.IPAddress.TryParse(host, out parsed))
            {
                MessageBox.Show(this, "Хост должен быть IP-адресом, например 127.0.0.1.", "Настройки",
                    MessageBoxButtons.OK, MessageBoxIcon.Warning);
                txtHost.Focus();
                return;
            }

            List<string> dcLines = new List<string>();
            foreach (string raw in txtDc.Text.Split(new char[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
            {
                string line = raw.Trim();
                if (line.Length == 0) continue;
                int colon = line.IndexOf(':');
                if (colon <= 0)
                {
                    MessageBox.Show(this, "Строка DC → IP должна быть в формате номер:IP, например 4:149.154.167.220.\n\nПроблемная строка: " + line,
                        "Настройки", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    txtDc.Focus();
                    return;
                }
                int dcNumber;
                System.Net.IPAddress dcIp;
                if (!int.TryParse(line.Substring(0, colon).Trim(), out dcNumber) ||
                    !System.Net.IPAddress.TryParse(line.Substring(colon + 1).Trim(), out dcIp))
                {
                    MessageBox.Show(this, "Не удалось разобрать строку DC → IP: " + line,
                        "Настройки", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    txtDc.Focus();
                    return;
                }
                dcLines.Add(dcNumber + ":" + dcIp.ToString());
            }

            int buf, pool, logMb;
            if (!int.TryParse(txtBuf.Text.Trim(), out buf) || buf < 4 ||
                !int.TryParse(txtPool.Text.Trim(), out pool) || pool < 0 ||
                !int.TryParse(txtLog.Text.Trim(), out logMb) || logMb < 1)
            {
                MessageBox.Show(this, "Буфер ≥ 4 КБ, пул ≥ 0, размер лога ≥ 1 МБ.", "Настройки",
                    MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            string oldStrategy = cfg.GetStr("zapret", "strategy", "fake-tls");
            string newStrategy = cbStrategy.SelectedItem == null ? oldStrategy : cbStrategy.SelectedItem.ToString();

            string oldSnapshot = Snapshot();

            cfg.Set("proxy", "host", host);
            cfg.Set("proxy", "port", port.ToString());
            cfg.Set("proxy", "dcmap", string.Join(",", dcLines.ToArray()));
            cfg.SetBool("proxy", "cfproxy", chkCf.Checked);
            cfg.SetBool("proxy", "h2", chkH2.Checked);
            cfg.SetBool("proxy", "nosecure", chkNoSecure.Checked);
            cfg.Set("proxy", "cfdomains", txtCf.Text.Trim());
            cfg.Set("proxy", "workerdomains", txtWorker.Text.Trim());
            cfg.Set("zapret", "strategy", newStrategy);
            cfg.Set("zapret", "tcpports", txtPorts.Text.Trim());
            cfg.Set("proxy", "bufkb", buf.ToString());
            cfg.Set("proxy", "poolsize", pool.ToString());
            cfg.Set("proxy", "logmaxmb", logMb.ToString());
            cfg.Save();

            StrategyChanged = oldStrategy != newStrategy;
            ProxySettingsChanged = oldSnapshot != Snapshot();
            DialogResult = DialogResult.OK;
            Close();
        }

        private string Snapshot()
        {
            return cfg.GetStr("proxy", "host", "") + "|" + cfg.GetStr("proxy", "port", "") + "|" +
                   cfg.GetStr("proxy", "dcmap", "") + "|" + cfg.GetStr("proxy", "cfproxy", "") + "|" +
                   cfg.GetStr("proxy", "h2", "") + "|" + cfg.GetStr("proxy", "nosecure", "") + "|" +
                   cfg.GetStr("proxy", "cfdomains", "") + "|" + cfg.GetStr("proxy", "workerdomains", "") + "|" +
                   cfg.GetStr("proxy", "bufkb", "") + "|" + cfg.GetStr("proxy", "poolsize", "") + "|" +
                   cfg.GetStr("proxy", "logmaxmb", "");
        }
    }

    internal sealed class MainForm : Form
    {
        private readonly Settings cfg;
        private readonly Proxy proxy;
        private readonly Zapret zapret;
        private NotifyIcon tray;
        private readonly System.Windows.Forms.Timer timer;

        private Label lblProxyState;
        private Label lblProxyDetail;
        private Label lblZapretState;
        private TextBox txtLog;
        private CheckBox chkAutostart;
        private CheckBox chkZapret;
        private Button btnStart;
        private Button btnStop;
        private bool suppressCheckEvents;
        private bool closeToTrayShown;
        private readonly bool startHidden;
        private readonly EventWaitHandle showEvent;

        public MainForm(Settings settings, bool startHidden, EventWaitHandle showEvent)
        {
            this.startHidden = startHidden;
            this.showEvent = showEvent;
            cfg = settings;
            proxy = new Proxy(settings);
            zapret = new Zapret(settings);

            Text = "TG WS Proxy";
            FormBorderStyle = FormBorderStyle.FixedSingle;
            MaximizeBox = false;
            StartPosition = FormStartPosition.CenterScreen;
            ClientSize = new Size(478, 508);
            Font = new Font("Segoe UI", 9f);
            Icon = LoadAppIcon();

            BuildUi();
            BuildTray();
            StartShowWatcher();

            if (startHidden && !cfg.GetBool("app", "traytip", false))
            {
                tray.ShowBalloonTip(4000, "TG WS Proxy",
                    "Работаю в трее. Если значка не видно — нажмите ^ рядом с часами.", ToolTipIcon.Info);
                cfg.SetBool("app", "traytip", true);
                cfg.Save();
            }

            timer = new System.Windows.Forms.Timer();
            timer.Interval = 2500;
            timer.Tick += delegate { RefreshState(); };
            timer.Start();

            Log.Line += OnLogLine;
            RefreshState();
            Log.Write("приложение запущено, папка: " + P.AppDir);
            if (!proxy.EngineFound) Log.Write("не найден TgWsProxy_windows.exe рядом с программой");
        }

        // Иконка из exe (в неё она вшита при сборке), с запасным вариантом на GDI+.
        private static Icon LoadAppIcon()
        {
            try
            {
                Icon embedded = System.Drawing.Icon.ExtractAssociatedIcon(Application.ExecutablePath);
                if (embedded != null) return embedded;
            }
            catch { }
            return MakeIcon();
        }

        private static Icon MakeIcon()
        {
            try
            {
                Bitmap bmp = new Bitmap(32, 32);
                using (Graphics g = Graphics.FromImage(bmp))
                {
                    g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
                    using (Brush b = new SolidBrush(Color.FromArgb(41, 120, 245))) g.FillEllipse(b, 1, 1, 30, 30);
                    using (Font f = new Font("Segoe UI", 16f, FontStyle.Bold, GraphicsUnit.Pixel))
                    using (Brush w = new SolidBrush(Color.White))
                    {
                        SizeF sz = g.MeasureString("T", f);
                        g.DrawString("T", f, w, (32 - sz.Width) / 2f, (32 - sz.Height) / 2f);
                    }
                }
                return Icon.FromHandle(bmp.GetHicon());
            }
            catch { return SystemIcons.Application; }
        }

        private void BuildUi()
        {
            Label title = new Label();
            title.Text = "TG WS Proxy";
            title.Font = new Font("Segoe UI", 15f, FontStyle.Bold);
            title.AutoSize = true;
            title.Location = new Point(16, 12);
            Controls.Add(title);

            Label sub = new Label();
            sub.Text = "локальный MTProto-прокси · без прав администратора";
            sub.ForeColor = Color.DimGray;
            sub.AutoSize = true;
            sub.Location = new Point(18, 44);
            Controls.Add(sub);

            lblProxyState = new Label();
            lblProxyState.Text = "прокси: проверяю…";
            lblProxyState.Font = new Font("Segoe UI", 10f, FontStyle.Bold);
            lblProxyState.AutoSize = true;
            lblProxyState.Location = new Point(18, 74);
            Controls.Add(lblProxyState);

            lblProxyDetail = new Label();
            lblProxyDetail.Text = "";
            lblProxyDetail.ForeColor = Color.DimGray;
            lblProxyDetail.AutoSize = true;
            lblProxyDetail.Location = new Point(18, 98);
            Controls.Add(lblProxyDetail);

            lblZapretState = new Label();
            lblZapretState.Text = "";
            lblZapretState.ForeColor = Color.DimGray;
            lblZapretState.AutoSize = true;
            lblZapretState.Location = new Point(18, 120);
            Controls.Add(lblZapretState);

            btnStart = MakeButton("Запустить", 18, 152, 118, 36);
            btnStart.Click += delegate { DoStart(); };
            Controls.Add(btnStart);

            btnStop = MakeButton("Остановить", 144, 152, 118, 36);
            btnStop.Click += delegate { proxy.Stop(); RefreshState(); };
            Controls.Add(btnStop);

            Button btnTg = MakeButton("Открыть в Telegram", 270, 152, 190, 36);
            btnTg.Click += delegate { OpenTelegram(); };
            Controls.Add(btnTg);

            chkAutostart = new CheckBox();
            chkAutostart.Text = "Запускать при входе в Windows";
            chkAutostart.AutoSize = true;
            chkAutostart.Location = new Point(18, 202);
            chkAutostart.Checked = Autostart.IsEnabled();
            chkAutostart.CheckedChanged += delegate
            {
                if (suppressCheckEvents) return;
                Autostart.SetEnabled(chkAutostart.Checked);
                Log.Write(chkAutostart.Checked ? "автозапуск включён" : "автозапуск выключен");
            };
            Controls.Add(chkAutostart);

            chkZapret = new CheckBox();
            chkZapret.Text = "Обход DPI (zapret) — нужен один раз UAC";
            chkZapret.AutoSize = true;
            chkZapret.Location = new Point(18, 228);
            chkZapret.CheckedChanged += delegate
            {
                if (suppressCheckEvents) return;
                ToggleZapret(chkZapret.Checked);
            };
            Controls.Add(chkZapret);

            Button btnTest = MakeButton("Проверка соединения", 18, 262, 170, 32);
            btnTest.Click += delegate { RunChecks(); };
            Controls.Add(btnTest);

            Button btnSettings = MakeButton("Настройки…", 196, 262, 120, 32);
            btnSettings.Click += delegate { OpenSettings(); };
            Controls.Add(btnSettings);

            Button btnLogs = MakeButton("Логи", 324, 262, 100, 32);
            btnLogs.Click += delegate { OpenInEditor(P.LogFile); };
            Controls.Add(btnLogs);

            txtLog = new TextBox();
            txtLog.Multiline = true;
            txtLog.ReadOnly = true;
            txtLog.ScrollBars = ScrollBars.Vertical;
            txtLog.Location = new Point(18, 306);
            txtLog.Size = new Size(442, 162);
            txtLog.BackColor = Color.FromArgb(248, 249, 251);
            txtLog.Font = new Font("Consolas", 8.25f);
            txtLog.BorderStyle = BorderStyle.FixedSingle;
            Controls.Add(txtLog);

            Label hint = new Label();
            hint.Text = "Закрытие сворачивает окно в панель задач; значок также в трее.";
            hint.ForeColor = Color.Gray;
            hint.AutoSize = true;
            hint.Location = new Point(18, 476);
            Controls.Add(hint);
        }

        private static Button MakeButton(string text, int x, int y, int w, int h)
        {
            Button b = new Button();
            b.Text = text;
            b.Location = new Point(x, y);
            b.Size = new Size(w, h);
            b.FlatStyle = FlatStyle.System;
            b.UseVisualStyleBackColor = true;
            return b;
        }

        private void BuildTray()
        {
            ContextMenuStrip menu = new ContextMenuStrip();
            menu.Items.Add("Показать", null, delegate { ShowFromTray(); });
            menu.Items.Add("Настройки…", null, delegate { ShowFromTray(); OpenSettings(); });
            menu.Items.Add("Копировать ссылку", null, delegate
            {
                try { Clipboard.SetText(proxy.TelegramLink()); Log.Write("ссылка скопирована"); }
                catch (Exception ex) { Log.Write("буфер обмена: " + ex.Message); }
            });
            menu.Items.Add("Свернуть в трей", null, delegate
            {
                Hide();
                tray.ShowBalloonTip(3000, "TG WS Proxy",
                    "Свёрнуто в трей. Запустите TgwsOne.exe ещё раз, чтобы вернуть окно.", ToolTipIcon.Info);
            });
            menu.Items.Add("Открыть в Telegram", null, delegate { OpenTelegram(); });
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add("Запустить прокси", null, delegate { DoStart(); });
            menu.Items.Add("Остановить прокси", null, delegate { proxy.Stop(); RefreshState(); });
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add("Логи", null, delegate { OpenInEditor(P.LogFile); });
            menu.Items.Add("Лог прокси", null, delegate { OpenInEditor(proxy.ProxyLogFile); });
            menu.Items.Add("Лог zapret (winws)", null, delegate { OpenInEditor(Path.Combine(P.LogsDir, "winws.log")); });
            menu.Items.Add("Сменить стратегию обхода DPI", null, delegate { CycleStrategy(); });
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add("Удалить обход DPI (UAC)…", null, delegate
            {
                if (MessageBox.Show(this, "Удалить задачу автозапуска обхода DPI?", "zapret",
                        MessageBoxButtons.YesNo, MessageBoxIcon.Question) == DialogResult.Yes)
                {
                    Zapret.RunElevated("--uninstall-zapret");
                    RefreshState();
                }
            });
            menu.Items.Add("Выход", null, delegate { Quit(); });

            try
            {
                tray = new NotifyIcon();
                tray.Icon = Icon;
                tray.Text = "TG WS Proxy";
                tray.ContextMenuStrip = menu;
                tray.DoubleClick += delegate { ShowFromTray(); };
                tray.Visible = true;
            }
            catch (Exception ex)
            {
                Log.Write("трей недоступен: " + ex.Message);
            }
        }

        private void StartShowWatcher()
        {
            if (showEvent == null) return;
            Thread watcher = new Thread(delegate()
            {
                while (true)
                {
                    try { showEvent.WaitOne(); }
                    catch { break; }
                    try { BeginInvoke((MethodInvoker)delegate { ShowFromTray(); }); }
                    catch { }
                }
            });
            watcher.IsBackground = true;
            watcher.Start();
        }

        [DllImport("user32.dll")]
        private static extern bool SetForegroundWindow(IntPtr hWnd);

        private void ShowFromTray()
        {
            Show();
            if (WindowState == FormWindowState.Minimized) WindowState = FormWindowState.Normal;
            Activate();
            try { SetForegroundWindow(Handle); } catch { }
            try { tray.Visible = true; } catch { }
        }

        private void Quit()
        {
            timer.Stop();
            tray.Visible = false;
            tray.Dispose();
            Application.Exit();
        }

        protected override void SetVisibleCore(bool value)
        {
            // --tray: окно не показываем, но handle создаём сразу —
            // иначе его нельзя показать по сигналу от второго запуска.
            if (!IsHandleCreated && startHidden)
            {
                CreateHandle();
                base.SetVisibleCore(false);
                return;
            }
            base.SetVisibleCore(value);
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            Log.Write("закрытие окна: " + e.CloseReason);
            if (e.CloseReason == CloseReason.UserClosing)
            {
                e.Cancel = true;
                // Сворачиваем в панель задач: окно всегда можно найти,
                // даже если Windows спрятала значок в «скрытых».
                try
                {
                    WindowState = FormWindowState.Minimized;
                    if (!closeToTrayShown)
                    {
                        closeToTrayShown = true;
                        tray.ShowBalloonTip(3500, "TG WS Proxy",
                            "Свёрнуто в панель задач. Значок также есть в трее (возможно, под ^).", ToolTipIcon.Info);
                    }
                }
                catch (Exception ex)
                {
                    Log.Write("сворачивание: " + ex.Message);
                }
                return;
            }
            base.OnFormClosing(e);
        }

        private static int FindFreePort(int from)
        {
            for (int port = from; port < from + 50; port++)
            {
                try
                {
                    System.Net.Sockets.TcpListener probe = new System.Net.Sockets.TcpListener(IPAddress.Loopback, port);
                    probe.Start();
                    probe.Stop();
                    return port;
                }
                catch { }
            }
            return from;
        }

        private static void OpenUrl(string url)
        {
            ThreadPool.QueueUserWorkItem(delegate { try { Process.Start(url); } catch { } });
        }

        private void OpenTelegram()
        {
            if (!proxy.EngineFound)
            {
                DialogResult r = MessageBox.Show(this,
                    "Рядом с программой нет TgWsProxy_windows.exe.\n\n" +
                    "Скачайте его со страницы релизов и положите в эту же папку.\n" +
                    "Открыть страницу релизов?",
                    "Нужен движок прокси", MessageBoxButtons.YesNo, MessageBoxIcon.Information);
                if (r == DialogResult.Yes) OpenUrl("https://github.com/Flowseal/tg-ws-proxy/releases");
                return;
            }
            proxy.OpenInTelegram();
        }

        private void DoStart()
        {
            if (!proxy.EngineFound)
            {
                DialogResult r = MessageBox.Show(this,
                    "Рядом с программой нет TgWsProxy_windows.exe.\n\n" +
                    "Скачайте файл со страницы релизов и положите его в эту же папку.\n" +
                    "Открыть страницу релизов?",
                    "Нужен движок прокси", MessageBoxButtons.YesNo, MessageBoxIcon.Information);
                if (r == DialogResult.Yes)
                    OpenUrl("https://github.com/Flowseal/tg-ws-proxy/releases");
                return;
            }
            btnStart.Enabled = false;
            bool ok;
            try { ok = proxy.Start(); }
            finally { btnStart.Enabled = true; RefreshState(); }

            if (!ok && proxy.ForeignInstances > 0)
            {
                MessageBox.Show(this,
                    "Уже запущен другой экземпляр TgWsProxy_windows.exe (например, старая копия прокси).\n\n" +
                    "Прокси не умеет работать в два экземпляра — второй просто показывает диалог.\n" +
                    "Закройте его (в трее или в диспетчере задач) и нажмите «Запустить» снова.",
                    "Прокси уже запущен", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            if (!ok && proxy.PortBusy)
            {
                int free = FindFreePort(proxy.Port + 1);
                DialogResult r = MessageBox.Show(this,
                    "Порт " + proxy.Port + " занят другим приложением (возможно, вторым экземпляром прокси).\n\n" +
                    "Переключиться на свободный порт " + free + "?\n" +
                    "(ссылка для Telegram обновится автоматически; «Нет» — откроет настройки)",
                    "Порт занят", MessageBoxButtons.YesNoCancel, MessageBoxIcon.Warning);

                if (r == DialogResult.Yes && free != proxy.Port)
                {
                    cfg.Set("proxy", "port", free.ToString());
                    cfg.Save();
                    proxy.WriteConfig();
                    Log.Write("порт изменён на " + free);
                    bool ok2;
                    btnStart.Enabled = false;
                    try { ok2 = proxy.Start(); }
                    finally { btnStart.Enabled = true; RefreshState(); }
                    if (!ok2) Log.Write("не удалось запустить на порту " + free);
                }
                else if (r == DialogResult.No)
                {
                    OpenSettings();
                }
            }
        }

        private void RefreshState()
        {
            zapret.RefreshTaskStateAsync();
            bool running = proxy.IsRunning;
            bool listening = proxy.IsListening;

            if (running && listening)
            {
                lblProxyState.Text = "прокси: работает";
                lblProxyState.ForeColor = Color.FromArgb(22, 130, 60);
            }
            else if (running)
            {
                lblProxyState.Text = "прокси: запускается…";
                lblProxyState.ForeColor = Color.FromArgb(180, 120, 0);
            }
            else if (proxy.PortBusy)
            {
                lblProxyState.Text = "прокси: остановлен (порт " + proxy.Port + " занят другим приложением)";
                lblProxyState.ForeColor = Color.FromArgb(180, 100, 0);
            }
            else
            {
                lblProxyState.Text = "прокси: остановлен";
                lblProxyState.ForeColor = Color.FromArgb(160, 40, 40);
            }

            lblProxyDetail.Text = proxy.Host + ":" + proxy.Port
                + "  ·  CF-прокси: " + (cfg.GetBool("proxy", "cfproxy", true) ? "вкл" : "выкл")
                + "  ·  H2: " + (cfg.GetBool("proxy", "h2", true) ? "вкл" : "выкл");

            bool zapretOn = zapret.WinwsRunning;
            if (zapret.MachineInstalled)
            {
                lblZapretState.Text = zapretOn
                    ? "обход DPI: работает для всех пользователей (" + zapret.Strategy + ")"
                    : "обход DPI: включён для всех, сейчас остановлен";
            }
            else if (!zapret.BundleFound) lblZapretState.Text = "обход DPI: папка zapret\\bin не найдена";
            else if (!zapret.TaskInstalled) lblZapretState.Text = "обход DPI: не установлен";
            else if (zapretOn) lblZapretState.Text = "обход DPI: работает (" + zapret.Strategy + ")";
            else lblZapretState.Text = "обход DPI: установлен, выключен";

            if (tray != null)
            {
                try { tray.Text = running ? ("TG WS Proxy — работает, " + proxy.Port) : "TG WS Proxy — остановлен"; }
                catch { }
            }

            suppressCheckEvents = true;
            chkZapret.Checked = zapretOn;
            chkAutostart.Checked = Autostart.IsEnabled();
            suppressCheckEvents = false;
        }

        private static readonly string[] Strategies = new string[] { "fake-tls", "split", "split-stealth", "aggressive" };

        private void OpenSettings()
        {
            using (SettingsForm dlg = new SettingsForm(cfg, proxy))
            {
                if (dlg.ShowDialog(this) != DialogResult.OK)
                {
                    RefreshState();
                    return;
                }

                Log.Write("настройки сохранены");

                if (dlg.StrategyChanged && zapret.QueryTask())
                {
                    Log.Write("стратегия изменена — перезапуск обхода DPI");
                    zapret.RequestStop();
                    zapret.StartViaTask();
                }

                if (proxy.IsRunning && dlg.ProxySettingsChanged)
                {
                    if (MessageBox.Show(this,
                            "Настройки сохранены.\nПерезапустить прокси сейчас, чтобы они применились?",
                            "TG WS Proxy", MessageBoxButtons.YesNo, MessageBoxIcon.Question) == DialogResult.Yes)
                    {
                        proxy.Stop();
                        proxy.Start();
                    }
                }
                RefreshState();
            }
        }

        private void CycleStrategy()
        {
            if (zapret.MachineInstalled)
            {
                if (Zapret.IsAdmin()) zapret.CycleMachineStrategy();
                else Zapret.RunElevated("--zapret-all-cycle");
                RefreshState();
                return;
            }

            string current = zapret.Strategy;
            int idx = Array.IndexOf(Strategies, current);
            string next = Strategies[(idx + 1) % Strategies.Length];
            cfg.Set("zapret", "strategy", next);
            cfg.Save();
            Log.Write("стратегия zapret: " + current + " -> " + next + " (перезапуск обхода)");
            if (zapret.QueryTask())
            {
                zapret.RequestStop();
                zapret.StartViaTask();
            }
            RefreshState();
        }

        private void ToggleZapret(bool enable)
        {
            if (zapret.MachineInstalled)
            {
                if (Zapret.IsAdmin()) zapret.SetMachineEnabled(enable);
                else Zapret.RunElevated(enable ? "--zapret-all-on" : "--zapret-all-off");
                RefreshState();
                return;
            }

            if (enable)
            {
                if (!zapret.BundleFound)
                {
                    DialogResult r = MessageBox.Show(this,
                        "Для обхода DPI нужен zapret: папка zapret\\bin с winws.exe.\n\n" +
                        "Скачать релиз и открыть инструкцию?",
                        "Нет zapret", MessageBoxButtons.YesNo, MessageBoxIcon.Information);
                    if (r == DialogResult.Yes)
                        OpenUrl("https://github.com/Flowseal/zapret-discord-youtube/releases");
                    suppressCheckEvents = true; chkZapret.Checked = false; suppressCheckEvents = false;
                    return;
                }

                if (!zapret.QueryTask())
                {
                    DialogResult r = MessageBox.Show(this,
                        "Обход DPI требует прав администратора: winws ставит драйвер WinDivert.\n\n" +
                        "Сейчас один раз подтвердите UAC — дальше обход будет запускаться вместе с системой\n" +
                        "и это приложение останется без прав администратора.\n\nПродолжить?",
                        "Один раз нужен UAC", MessageBoxButtons.YesNo, MessageBoxIcon.Question);
                    if (r != DialogResult.Yes)
                    {
                        suppressCheckEvents = true; chkZapret.Checked = false; suppressCheckEvents = false;
                        return;
                    }
                    int code = Zapret.RunElevated("--install-zapret");
                    bool ok = zapret.QueryTask();
                    Log.Write("установка обхода DPI: " + (ok ? "успешно" : "не удалось, код " + code));
                    if (!ok)
                    {
                        suppressCheckEvents = true; chkZapret.Checked = false; suppressCheckEvents = false;
                        return;
                    }
                }
                else
                {
                    zapret.StartViaTask();
                }
            }
            else
            {
                zapret.RequestStop();
            }
            RefreshState();
        }

        private void RunChecks()
        {
            Log.Write("проверка соединения…");
            ThreadPool.QueueUserWorkItem(delegate
            {
                Check("TCP 443 → cloudflare.com", delegate { return Tcp("cloudflare.com", 443); });
                Check("HTTPS cloudflare.com", delegate { return Https("https://www.cloudflare.com/cdn-cgi/trace") == 200; });
                Check("TCP 443 → web.telegram.org", delegate { return Tcp("web.telegram.org", 443); });
                Check("HTTPS web.telegram.org", delegate { return Https("https://web.telegram.org/") == 200; });
                Log.Write("если TCP проходит, а HTTPS нет — режет DPI: включите «Обход DPI (zapret)».");
            });
        }

        private static void Check(string title, Func<bool> test)
        {
            bool ok;
            try { ok = test(); } catch { ok = false; }
            Log.Write((ok ? "  OK   " : "  FAIL ") + title);
        }

        private static bool Tcp(string host, int port)
        {
            try
            {
                System.Net.Sockets.TcpClient c = new System.Net.Sockets.TcpClient();
                IAsyncResult ar = c.BeginConnect(host, port, null, null);
                bool ok = ar.AsyncWaitHandle.WaitOne(4000);
                if (ok) c.EndConnect(ar);
                c.Close();
                return ok;
            }
            catch { return false; }
        }

        private static int Https(string url)
        {
            try
            {
                System.Net.ServicePointManager.SecurityProtocol = System.Net.SecurityProtocolType.Tls12;
                System.Net.HttpWebRequest req = (System.Net.HttpWebRequest)System.Net.WebRequest.Create(url);
                req.Timeout = 10000;
                req.AllowAutoRedirect = true;
                using (System.Net.HttpWebResponse resp = (System.Net.HttpWebResponse)req.GetResponse())
                    return (int)resp.StatusCode;
            }
            catch
            {
                return 0;
            }
        }

        private void OpenInEditor(string path)
        {
            ThreadPool.QueueUserWorkItem(delegate
            {
                try
                {
                    if (!File.Exists(path)) { File.WriteAllText(path, ""); }
                    ProcessStartInfo psi = new ProcessStartInfo("notepad.exe", "\"" + path + "\"");
                    psi.UseShellExecute = false;
                    Process.Start(psi);
                }
                catch (Exception ex) { Log.Write("не удалось открыть " + path + ": " + ex.Message); }
            });
        }

        private void OnLogLine(string line)
        {
            if (txtLog.IsDisposed) return;
            if (txtLog.InvokeRequired)
            {
                txtLog.BeginInvoke((MethodInvoker)delegate { OnLogLine(line); });
                return;
            }
            if (txtLog.Lines.Length > 500)
            {
                txtLog.Text = string.Join("\r\n", txtLog.Lines, txtLog.Lines.Length - 300, 300);
                txtLog.SelectionStart = txtLog.TextLength;
                txtLog.ScrollToCaret();
            }
            txtLog.AppendText(line + "\r\n");
        }
    }

    // ------------------------------------------------------------------ один экземпляр

    internal static class Instance
    {
        public const string MutexName = "TgwsOne_SingleInstance_v2";
        private const string EventName = "TgwsOne_ShowWindow_v2";

        public static EventWaitHandle CreateShowEvent()
        {
            try { return new EventWaitHandle(false, EventResetMode.AutoReset, EventName); }
            catch { return null; }
        }

        public static bool SignalShow()
        {
            try
            {
                using (EventWaitHandle e = EventWaitHandle.OpenExisting(EventName)) { e.Set(); return true; }
            }
            catch { return false; }
        }
    }
    // ------------------------------------------------------------------ автозапуск (HKCU, без админа)

    internal static class Autostart
    {
        private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
        private const string ValueName = "TgwsOne";

        public static bool IsEnabled()
        {
            try
            {
                using (RegistryKey k = Registry.CurrentUser.OpenSubKey(RunKey))
                {
                    if (k == null) return false;
                    string v = k.GetValue(ValueName) as string;
                    return v != null && v.IndexOf(P.Exe, StringComparison.OrdinalIgnoreCase) >= 0;
                }
            }
            catch { return false; }
        }

        public static void SetEnabled(bool enabled)
        {
            try
            {
                using (RegistryKey k = Registry.CurrentUser.CreateSubKey(RunKey))
                {
                    if (k == null) return;
                    if (enabled) k.SetValue(ValueName, "\"" + P.Exe + "\" --tray");
                    else k.DeleteValue(ValueName, false);
                }
            }
            catch (Exception ex) { Log.Write("автозапуск: " + ex.Message); }
        }
    }

    // ------------------------------------------------------------------ точка входа

    internal static class Program
    {
        [DllImport("user32.dll")] private static extern bool SetProcessDPIAware();

        [STAThread]
        private static int Main(string[] args)
        {
            try { SetProcessDPIAware(); } catch { }

            string mode = args.Length > 0 ? args[0].ToLowerInvariant() : "";
            if (mode == "--selftest") return SelfTest();
            if (mode == "--zapret-start") { Settings s0 = new Settings(); s0.Load(); return new Zapret(s0).RunSupervisor(); }
            if (mode == "--install-zapret")
            {
                Settings s1 = new Settings(); s1.Load(); Lists.EnsureAll();
                return new Zapret(s1).Install() ? 0 : 1;
            }
            if (mode == "--uninstall-zapret")
            {
                Settings s2 = new Settings(); s2.Load();
                return new Zapret(s2).Uninstall() ? 0 : 1;
            }
            if (mode == "--show")
            {
                if (Instance.SignalShow()) return 0;
                mode = "";
            }
            if (mode == "--ui-test")
            {
                // дымовой тест интерфейса: построить окна и сразу закрыть
                Settings sui = new Settings(); sui.Load(); Lists.EnsureAll();
                Proxy pui = new Proxy(sui);
                Directory.CreateDirectory(P.LogsDir);
                using (Form main = new MainForm(sui, false, null))
                {
                    main.CreateControl();
                    main.Show();
                    Application.DoEvents();
                    using (Bitmap bmp = new Bitmap(main.Width, main.Height))
                    {
                        main.DrawToBitmap(bmp, new Rectangle(0, 0, main.Width, main.Height));
                        bmp.Save(Path.Combine(P.LogsDir, "ui-main.png"), System.Drawing.Imaging.ImageFormat.Png);
                    }
                }
                using (SettingsForm dlg = new SettingsForm(sui, pui))
                {
                    dlg.CreateControl();
                    dlg.Show();
                    Application.DoEvents();
                    using (Bitmap bmp2 = new Bitmap(dlg.Width, dlg.Height))
                    {
                        dlg.DrawToBitmap(bmp2, new Rectangle(0, 0, dlg.Width, dlg.Height));
                        bmp2.Save(Path.Combine(P.LogsDir, "ui-settings.png"), System.Drawing.Imaging.ImageFormat.Png);
                    }
                    dlg.Close();
                }
                return 0;
            }
            if (mode == "--task-xml-all")
            {
                Directory.CreateDirectory(P.LogsDir);
                string pathAll = Path.Combine(P.LogsDir, "task-all.xml");
                File.WriteAllText(pathAll, Zapret.BuildTaskXml(true), Encoding.Unicode);
                Say(pathAll);
                return 0;
            }
            if (mode == "--task-xml")
            {
                string path = Path.Combine(P.LogsDir, "task-check.xml");
                Directory.CreateDirectory(P.LogsDir);
                File.WriteAllText(path, Zapret.BuildTaskXml(), Encoding.Unicode);
                Say(path);
                return 0;
            }
            if (mode == "--ensure-zapret-task") return Zapret.EnsureTask() ? 0 : 1;
            if (mode == "--install-zapret-all")
            {
                Settings sa = new Settings(); sa.Load();
                return new Zapret(sa).EnsureMachineTask() ? 0 : 1;
            }
            if (mode == "--remove-zapret-all")
            {
                Settings sr = new Settings(); sr.Load();
                new Zapret(sr).RemoveMachineTask();
                return 0;
            }
            if (mode == "--zapret-all-on")
            {
                Settings son = new Settings(); son.Load();
                new Zapret(son).SetMachineEnabled(true);
                return 0;
            }
            if (mode == "--zapret-all-off")
            {
                Settings soff = new Settings(); soff.Load();
                new Zapret(soff).SetMachineEnabled(false);
                return 0;
            }
            if (mode == "--zapret-all-cycle")
            {
                Settings scy = new Settings(); scy.Load();
                return new Zapret(scy).CycleMachineStrategy() ? 0 : 1;
            }
            if (mode == "--remove-zapret-task") return Zapret.RemoveTask() ? 0 : 1;
            if (mode == "--start" || mode == "--stop" || mode == "--status")
            {
                Settings sc = new Settings(); sc.Load(); Lists.EnsureAll();
                Proxy pc = new Proxy(sc);
                if (mode == "--start") { bool ok = pc.Start(); Say(ok ? "запущен" : "не удалось запустить"); return ok ? 0 : 2; }
                if (mode == "--stop") { pc.Stop(); Say("остановлен"); return 0; }
                Say("running=" + pc.IsRunning + " listening=" + pc.IsListening + " port=" + pc.Port);
                return pc.IsListening ? 0 : 1;
            }

            bool single;
            Mutex mutex = new Mutex(true, Instance.MutexName, out single);
            if (!single)
            {
                // Второй запуск не ругается, а поднимает окно первого экземпляра.
                if (Instance.SignalShow())
                {
                    for (int i = 0; i < 30; i++)
                    {
                        Thread.Sleep(100);
                        if (AnyVisibleWindow()) break;
                    }
                    return 0;
                }
                MessageBox.Show(
                    "TG WS Proxy уже запущен, но на связь не отвечает.\n\n" +
                    "Значок программы ищите в области уведомлений — возможно, он в скрытых (нажмите ^ рядом с часами).\n" +
                    "Если окна нет: завершите TgwsOne.exe в диспетчере задач и запустите заново.",
                    "TG WS Proxy", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return 0;
            }

            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);

            Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
            Application.ThreadException += delegate(object sender, System.Threading.ThreadExceptionEventArgs ea)
            {
                Log.Write("ОШИБКА UI: " + ea.Exception);
            };
            AppDomain.CurrentDomain.UnhandledException += delegate(object sender, UnhandledExceptionEventArgs ea)
            {
                Log.Write("ФАТАЛЬНАЯ ОШИБКА: " + ea.ExceptionObject);
            };

            Settings settings = new Settings();
            settings.Load();
            Lists.EnsureAll();

            EventWaitHandle showEvent = Instance.CreateShowEvent();
            MainForm form = new MainForm(settings, mode == "--tray", showEvent);

            Application.Run(form);
            GC.KeepAlive(mutex);
            return 0;
        }

        private static bool AnyVisibleWindow()
        {
            foreach (Process p in Process.GetProcessesByName("TgwsOne"))
            {
                try { if (p.MainWindowHandle != IntPtr.Zero) return true; }
                catch { }
            }
            return false;
        }

        private static int SelfTest()
        {
            StringBuilder sb = new StringBuilder();
            int errors = 0;
            try
            {
                Settings s = new Settings();
                s.Load();
                Lists.EnsureAll();
                Proxy p = new Proxy(s);
                Zapret z = new Zapret(s);

                sb.AppendLine("app dir:     " + P.AppDir);
                sb.AppendLine("engine:      " + (p.EngineFound ? "OK " + P.Engine : "MISSING"));
                sb.AppendLine("engine used: " + p.ActiveEngine);
                sb.AppendLine("proxy log:   " + p.ProxyLogFile);
                sb.AppendLine("app writable:" + P.AppDirWritable);
                sb.AppendLine("data root:   " + P.DataRoot);
                sb.AppendLine("ini:         " + P.Ini + " (port=" + p.Port + ", secret len=" + p.Secret.Length + ")");
                sb.AppendLine("lists:       " + P.ListsDir);
                sb.AppendLine("proxy:       running=" + p.IsRunning + " listening=" + p.IsListening + " portBusy=" + p.PortBusy + " foreign=" + p.ForeignInstances);
                sb.AppendLine("zapret:      bundle=" + z.BundleFound + " task=" + z.QueryTask() + " winws=" + z.WinwsRunning);
                sb.AppendLine("zapret cmd:  " + z.CommandLine());
                sb.AppendLine("autostart:   " + Autostart.IsEnabled());
                sb.AppendLine("link:        " + p.TelegramLink());
                p.WriteConfig();
                string cfgPath = p.ProxyConfigFile;
                sb.AppendLine("config:      " + cfgPath + " (" + (File.Exists(cfgPath) ? new FileInfo(cfgPath).Length + " b" : "нет файла") + ")");
                if (!p.EngineFound) errors++;
            }
            catch (Exception ex)
            {
                sb.AppendLine("EXCEPTION: " + ex);
                errors++;
            }
            sb.AppendLine(errors == 0 ? "RESULT: OK" : "RESULT: " + errors + " problem(s)");
            try { Directory.CreateDirectory(P.LogsDir); File.WriteAllText(Path.Combine(P.LogsDir, "selftest.log"), sb.ToString(), Encoding.UTF8); }
            catch { }
            try
            {
                if (AttachConsole(-1))
                {
                    Console.OutputEncoding = Encoding.UTF8;
                    Console.WriteLine(sb.ToString());
                }
            }
            catch { }
            return errors;
        }

        private static void Say(string text)
        {
            try
            {
                if (AttachConsole(-1))
                {
                    Console.OutputEncoding = Encoding.UTF8;
                    Console.WriteLine(text);
                }
            }
            catch { }
        }

        [DllImport("kernel32.dll")] private static extern bool AttachConsole(int processId);
    }
}
