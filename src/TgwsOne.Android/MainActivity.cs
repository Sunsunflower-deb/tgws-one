using Android.App;
using Android.Content;
using Android.Content.PM;
using Android.OS;
using Avalonia.Android;
using TgwsOne.Core;
using TgwsOne.Engine;
using TgwsOne.Engine.Desync;
using TgwsOne.Mobile;
using TgwsOne.Mobile.Views;
using AndroidLog = Android.Util.Log;

namespace TgwsOne.Android;

/// <summary>
/// The one window of the phone application. All screens are Avalonia views from <c>TgwsOne.Mobile</c>; this activity
/// only builds the services those views talk to and hands the system's back button to the shell's stack.
/// </summary>
[Activity(
    Name = "io.github.sunsunflowerdeb.tgwsone.MainActivity",
    MainLauncher = true,
    Exported = true,
    Label = "TgwsOne",
    Theme = "@style/Theme.AppCompat.DayNight.NoActionBar")]
public sealed class MainActivity : AvaloniaMainActivity
{
    private const int NotificationPermissionRequest = 1;
    private const string UiPreferences = "ui";
    private const string WelcomeDoneKey = "welcome_done";

    private MobileShellViewModel? _shell;

    protected override void OnCreate(Bundle? savedInstanceState)
    {
        base.OnCreate(savedInstanceState);

        _shell = CreateShell();
        Content = new MobileShellView { DataContext = _shell };
        BackRequested += OnBackRequested;
        RequestNotificationPermission();
    }

    protected override void OnDestroy()
    {
        BackRequested -= OnBackRequested;
        if (_shell?.Current is LogsViewModel logs) logs.OnHidden();
        base.OnDestroy();
    }

    private MobileShellViewModel CreateShell()
    {
        MobileStorage storage = MobileStorage.For(FilesDir!.AbsolutePath);
        var settings = new FileMobileSettings(storage);
        var proxy = new ServiceProxyControl(this);
        var desync = new RuntimeDesyncControl(storage);
        var launcher = new AndroidLauncher(this);
        var clipboard = new AndroidClipboard(this);
        var share = new AndroidShare(this);
        var probes = new MobileEgressProbes(storage, desync,
            (timeout, ct) => EngineRuntime.SelfCheckAsync(timeout, ct));

        var actions = new ShellDiagnosticsActions(proxy, desync, settings, launcher);
        var home = new HomeViewModel(proxy, settings, () => EngineRuntime.ActiveConnections);
        var bypass = new BypassViewModel(desync, settings);
        var finder = new FinderViewModel(new DesyncFinder(desync, probes));
        var diagnostics = new DiagnosticsViewModel(
            new Diagnostics(proxy, desync, probes, () => true, () => EngineRuntime.ActiveConnections), actions);
        var logs = new LogsViewModel(LogsViewModel.DefaultTabs(storage), new SystemTicker(), clipboard, launcher);
        var settingsPage = new SettingsViewModel(settings);
        var connect = new ConnectViewModel(settings, launcher, clipboard, share);

        ISharedPreferences? preferences = GetSharedPreferences(UiPreferences, FileCreationMode.Private);
        bool firstRun = preferences?.GetBoolean(WelcomeDoneKey, false) != true;

        var shell = new MobileShellViewModel(home, bypass, finder, diagnostics, logs, settingsPage, connect,
            firstRun, () =>
            {
                if (preferences is null) return;
                using ISharedPreferencesEditor editor = preferences.Edit()!;
                editor.PutBoolean(WelcomeDoneKey, true);
                editor.Apply();
            });
        actions.Shell = shell;
        return shell;
    }

    private void OnBackRequested(object? sender, AndroidBackRequestedEventArgs e)
    {
        if (_shell?.GoBack() == true) e.Handled = true;
    }

    private void RequestNotificationPermission()
    {
        if (!OperatingSystem.IsAndroidVersionAtLeast(33)) return;
        if (CheckSelfPermission(global::Android.Manifest.Permission.PostNotifications) == Permission.Granted) return;
        RequestPermissions([global::Android.Manifest.Permission.PostNotifications], NotificationPermissionRequest);
    }

    /// <summary>
    /// Starts and stops the foreground service, which owns the engine. The home page sees only this interface, so
    /// there is never a second listener in the process.
    /// </summary>
    private sealed class ServiceProxyControl(MainActivity activity) : IProxyEngineControl
    {
        private static readonly TimeSpan StartTimeout = TimeSpan.FromSeconds(15);
        private static readonly TimeSpan StopTimeout = TimeSpan.FromSeconds(10);

        public async Task<EngineStartResult> StartAsync(CancellationToken ct = default)
        {
            if (EngineRuntime.IsRunning)
                return new EngineStartResult(EngineStartOutcome.AlreadyRunning, EngineRuntime.Port);

            ProxyService.Start(activity);
            DateTime deadline = DateTime.UtcNow + StartTimeout;
            while (DateTime.UtcNow < deadline)
            {
                if (EngineRuntime.IsRunning)
                    return new EngineStartResult(EngineStartOutcome.Started, EngineRuntime.Port);
                if (EngineRuntime.Error is { Length: > 0 } error)
                    return new EngineStartResult(EngineStartOutcome.EngineExited, EngineRuntime.Port, Output: error);
                await Task.Delay(50, ct);
            }
            return new EngineStartResult(EngineStartOutcome.Timeout, EngineRuntime.Port);
        }

        public async Task<EngineStopOutcome> StopAsync(CancellationToken ct = default)
        {
            if (!EngineRuntime.IsRunning) return EngineStopOutcome.NotRunning;

            ProxyService.Stop(activity);
            DateTime deadline = DateTime.UtcNow + StopTimeout;
            while (DateTime.UtcNow < deadline)
            {
                if (!EngineRuntime.IsRunning) return EngineStopOutcome.Stopped;
                await Task.Delay(50, ct);
            }
            return EngineStopOutcome.Stopped;
        }

        public EngineProxyStatus GetStatus()
        {
            bool running = EngineRuntime.IsRunning;
            int port = EngineRuntime.Port;
            return new EngineProxyStatus(running, running && port > 0, port, PortBusyByOther: false,
                                         Pid: running ? System.Environment.ProcessId : null);
        }

        public bool RestartNeeded() => false;
    }

    /// <summary>The live controller from the service's engine, with the same rules as the desktop's mobile host.</summary>
    private sealed class RuntimeDesyncControl(MobileStorage storage) : IDesyncControl
    {
        public IReadOnlyList<DesyncStrategy> Rotation
            => EngineRuntime.Desync?.Rotation
               ?? DesyncRotations.ForOperator(AndroidSettingsStore.Load(IniFile.Load(storage.IniPath)).Provider);

        public IReadOnlyList<AutoHost> AutoHosts
            => EngineRuntime.Desync is { IsAuto: true } live ? live.AutoHosts : [];

        public Task<FinderBaseline> CaptureAsync(CancellationToken ct = default)
        {
            if (EngineRuntime.Desync is { } live)
            {
                string id = live.Selected?.Id ?? DesyncFinder.AutoId;
                return Task.FromResult(new FinderBaseline(id, id != DesyncFinder.NoBypassId));
            }

            AndroidSettings settings = AndroidSettingsStore.Load(IniFile.Load(storage.IniPath));
            return Task.FromResult(new FinderBaseline(settings.Strategy, false));
        }

        public Task<string?> ApplyAsync(string candidateId, CancellationToken ct = default)
        {
            try
            {
                DesyncController? controller = EngineRuntime.Desync;
                if (controller is null) return Task.FromResult<string?>("движок прокси не запущен");

                if (candidateId == DesyncFinder.NoBypassId)
                {
                    controller.Select(new DesyncStrategy(DesyncFinder.NoBypassId, []));
                    return Task.FromResult<string?>(null);
                }
                if (candidateId == DesyncFinder.AutoId)
                {
                    controller.UseAuto();
                    return Task.FromResult<string?>(null);
                }

                if (!DesyncFinder.TryParseCandidate(candidateId, out string id, out int? ttl))
                    return Task.FromResult<string?>($"непонятный кандидат: {candidateId}");

                DesyncStrategy? strategy = Rotation.FirstOrDefault(s => s.Id == id) ?? BuiltInDesync.Find(id);
                if (strategy is null) return Task.FromResult<string?>($"нет такой стратегии: {id}");
                if (ttl is { } value) strategy = DesyncFinder.WithFakeTtl(strategy, value);

                controller.Select(strategy);
                return Task.FromResult<string?>(null);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException
                                           or InvalidOperationException)
            {
                return Task.FromResult<string?>(ex.Message);
            }
        }

        public Task<string?> RestoreAsync(FinderBaseline baseline, CancellationToken ct = default)
        {
            try
            {
                if (!baseline.Enabled)
                {
                    EngineRuntime.Desync = null;
                    return Task.FromResult<string?>(null);
                }

                DesyncController controller = EngineRuntime.Desync ?? new DesyncController();
                EngineRuntime.Desync = controller;
                if (baseline.Strategy == DesyncFinder.AutoId || BuiltInDesync.Find(baseline.Strategy) is not { } strategy)
                    controller.UseAuto();
                else
                    controller.Select(strategy);

                return Task.FromResult<string?>(null);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException
                                           or InvalidOperationException)
            {
                return Task.FromResult<string?>(ex.Message);
            }
        }

        public DesyncStrategy? Resolve(string sni, string route) => EngineRuntime.Desync?.Resolve(sni, route);
    }

    private sealed class ShellDiagnosticsActions(ServiceProxyControl proxy, RuntimeDesyncControl desync,
                                                  FileMobileSettings settings, AndroidLauncher launcher)
        : IDiagnosticsActions
    {
        public MobileShellViewModel? Shell { get; set; }

        public async Task<string?> ConnectAsync()
        {
            EngineStartResult result = await proxy.StartAsync();
            return result.Ok ? null : "Не удалось запустить прокси.";
        }

        public async Task<string?> OpenInTelegramAsync()
            => await launcher.OpenAsync(settings.TelegramLink())
                ? null
                : "Не удалось открыть Telegram. Скопируйте ссылку на странице «Подключение к Telegram».";

        public async Task<string?> EnableBypassAsync()
        {
            string strategy = settings.LoadAndroid().Strategy;
            if (strategy.Length == 0 || strategy == DesyncFinder.NoBypassId) strategy = DesyncFinder.AutoId;
            return await desync.ApplyAsync(strategy);
        }

        public Task<string?> FindStrategyAsync()
        {
            Shell?.ShowFinder();
            return Task.FromResult<string?>(null);
        }

        public Task<string?> ChangePortAsync()
            => Task.FromResult<string?>("Свободный порт выбирается при запуске; отдельной смены порта на телефоне нет.");
    }

    private sealed class AndroidLauncher(MainActivity activity) : ILauncher
    {
        public Task<bool> OpenAsync(string url)
        {
            try
            {
                if (!url.StartsWith("tg:", StringComparison.OrdinalIgnoreCase))
                    return Task.FromResult(false);

                var intent = new Intent(Intent.ActionView, global::Android.Net.Uri.Parse(url));
                intent.AddFlags(ActivityFlags.NewTask);
                activity.StartActivity(intent);
                return Task.FromResult(true);
            }
            catch (ActivityNotFoundException)
            {
                Toast.MakeText(activity, "Telegram не установлен", ToastLength.Long)?.Show();
                return Task.FromResult(false);
            }
        }
    }

    private sealed class AndroidClipboard(MainActivity activity) : IClipboardService
    {
        public Task SetTextAsync(string text)
        {
            var manager = (ClipboardManager?)activity.GetSystemService(Context.ClipboardService);
            if (manager is not null) manager.PrimaryClip = ClipData.NewPlainText("TgwsOne", text);
            return Task.CompletedTask;
        }
    }

    private sealed class AndroidShare(MainActivity activity) : IShareService
    {
        public Task ShareAsync(string text)
        {
            try
            {
                var send = new Intent(Intent.ActionSend);
                send.SetType("text/plain");
                send.PutExtra(Intent.ExtraText, text);
                Intent chooser = Intent.CreateChooser(send, "Поделиться ссылкой")!;
                chooser.AddFlags(ActivityFlags.NewTask);
                activity.StartActivity(chooser);
            }
            catch (ActivityNotFoundException ex)
            {
                AndroidLog.Warn("TgwsOne", "share failed: " + ex.Message);
            }
            return Task.CompletedTask;
        }
    }
}
