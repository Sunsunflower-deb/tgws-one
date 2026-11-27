using Android.App;
using Android.Content;
using Android.Content.PM;
using Android.Graphics.Drawables;
using Android.Net;
using Android.OS;
using Android.Telephony;
using AndroidLog = Android.Util.Log;

namespace TgwsOne.Android;

/// <summary>
/// Keeps the proxy alive while the screen is off and the task is swiped away. The engine runs in this process;
/// the notification is the only control the user has once the activity is gone, so stopping it must free the port.
/// </summary>
[Service(
    Name = "io.github.sunsunflowerdeb.tgwsone.ProxyService",
    Exported = false,
    ForegroundServiceType = ForegroundService.TypeSpecialUse)]
public sealed class ProxyService : Service
{
    public const string ActionStop = "io.github.sunsunflowerdeb.tgwsone.action.STOP";

    private const string ChannelId = "proxy";
    private const int NotificationId = 1443;

    private readonly object _networkGate = new();
    private ConnectivityManager? _connectivity;
    private TelephonyManager? _telephony;
    private ConnectivityManager.NetworkCallback? _networkCallback;
    private Network? _currentNetwork;

    public static void Start(Context context) =>
        context.StartForegroundService(new Intent(context, typeof(ProxyService)));

    public static void Stop(Context context) =>
        context.StartService(new Intent(context, typeof(ProxyService)).SetAction(ActionStop));

    public override void OnCreate()
    {
        base.OnCreate();
        CreateChannel();
        RegisterNetworkCallback();
        RustEngineHost.Changed += OnRuntimeChanged;
    }

    public override StartCommandResult OnStartCommand(Intent? intent, StartCommandFlags flags, int startId)
    {
        if (intent?.Action == ActionStop)
        {
            Task.Run(async () =>
            {
                await RustEngineHost.StopAsync();
                StopForeground(StopForegroundFlags.Remove);
                StopSelf();
            });
            return StartCommandResult.NotSticky;
        }

        Notification starting = BuildNotification("Запуск движка…");
        // The specialUse type exists only from Android 14; older releases take the type from the manifest (where
        // it is unknown to them) and simply run a plain foreground service.
        if (OperatingSystem.IsAndroidVersionAtLeast(34))
            StartForeground(NotificationId, starting, ForegroundService.TypeSpecialUse);
        else
            StartForeground(NotificationId, starting);
        Task.Run(async () =>
        {
            try
            {
                await RustEngineHost.StartAsync(FilesDir!.AbsolutePath, CurrentOperatorMccMnc());
                UpdateNotification();
            }
            catch (Exception ex)
            {
                UpdateNotification("Ошибка: " + ex.Message);
                AndroidLog.Error("TgwsOne", "service start failed: " + ex);
                StopSelf();
            }
        });

        // START_STICKY: the system recreates the service after a low-memory kill, and the engine re-reads the
        // settings from files/ itself.
        return StartCommandResult.Sticky;
    }

    public override void OnDestroy()
    {
        UnregisterNetworkCallback();
        RustEngineHost.Changed -= OnRuntimeChanged;
        try
        {
            RustEngineHost.StopBlocking();
        }
        catch (Exception ex)
        {
            AndroidLog.Warn("TgwsOne", "engine teardown on destroy: " + ex.Message);
        }
        base.OnDestroy();
    }

    public override IBinder? OnBind(Intent? intent) => null;

    private void RegisterNetworkCallback()
    {
        _connectivity = GetSystemService(ConnectivityService) as ConnectivityManager;
        _telephony = GetSystemService(TelephonyService) as TelephonyManager;
        if (_connectivity is null) return;

        _networkCallback = new DefaultNetworkCallback(this);
        try
        {
            _connectivity.RegisterDefaultNetworkCallback(_networkCallback);
        }
        catch (Exception ex)
        {
            AndroidLog.Warn("TgwsOne", "network callback registration: " + ex.Message);
        }
    }

    private void UnregisterNetworkCallback()
    {
        if (_networkCallback is null) return;
        try
        {
            _connectivity?.UnregisterNetworkCallback(_networkCallback);
        }
        catch (Exception ex)
        {
            AndroidLog.Warn("TgwsOne", "network callback removal: " + ex.Message);
        }
        _networkCallback = null;
    }

    private string? CurrentOperatorMccMnc(Network? network = null)
    {
        network ??= _connectivity?.ActiveNetwork;
        if (network is null) return null;
        NetworkCapabilities? capabilities = _connectivity?.GetNetworkCapabilities(network);
        if (capabilities?.HasTransport(TransportType.Cellular) != true) return null;
        string? mccMnc = _telephony?.NetworkOperator;
        return string.IsNullOrWhiteSpace(mccMnc) ? null : mccMnc;
    }

    private void OnDefaultNetworkAvailable(Network network)
    {
        bool changed;
        lock (_networkGate)
        {
            changed = _currentNetwork is null || !_currentNetwork.Equals(network);
            _currentNetwork = network;
        }
        if (changed) RustEngineHost.OnNetworkChanged(CurrentOperatorMccMnc(network));
    }

    private void OnDefaultNetworkLost(Network network)
    {
        bool changed;
        lock (_networkGate)
        {
            changed = _currentNetwork is not null && _currentNetwork.Equals(network);
            if (changed) _currentNetwork = null;
        }
        if (changed) RustEngineHost.OnNetworkChanged(null);
    }

    private sealed class DefaultNetworkCallback(ProxyService owner) : ConnectivityManager.NetworkCallback
    {
        public override void OnAvailable(Network network) => owner.OnDefaultNetworkAvailable(network);

        public override void OnLost(Network network) => owner.OnDefaultNetworkLost(network);
    }

    private void OnRuntimeChanged()
    {
        try { UpdateNotification(); }
        catch (Exception ex) { AndroidLog.Warn("TgwsOne", "notification update: " + ex.Message); }
    }

    private void UpdateNotification(string? text = null)
    {
        string state = text
            ?? (RustEngineHost.IsRunning
                ? "Работает: " + RustEngineHost.Link
                : RustEngineHost.Error is { Length: > 0 } error ? "Ошибка: " + error : "Остановлен");
        ((NotificationManager)GetSystemService(NotificationService)!).Notify(NotificationId, BuildNotification(state));
    }

    private Notification BuildNotification(string text)
    {
        PendingIntent open = PendingIntent.GetActivity(
            this, 0, new Intent(this, typeof(MainActivity)),
            PendingIntentFlags.UpdateCurrent | PendingIntentFlags.Immutable)!;
        PendingIntent stop = PendingIntent.GetService(
            this, 1, new Intent(this, typeof(ProxyService)).SetAction(ActionStop),
            PendingIntentFlags.UpdateCurrent | PendingIntentFlags.Immutable)!;

        return new Notification.Builder(this, ChannelId)
            .SetContentTitle("TgwsOne")
            .SetContentText(text)
            .SetSmallIcon(global::Android.Resource.Drawable.StatSysDownload)
            .SetOngoing(true)
            .SetOnlyAlertOnce(true)
            .SetContentIntent(open)
            .AddAction(new Notification.Action.Builder(
                Icon.CreateWithResource(this, global::Android.Resource.Drawable.IcDelete), "Остановить", stop).Build())
            .Build();
    }

    private void CreateChannel()
    {
        var manager = (NotificationManager)GetSystemService(NotificationService)!;
        if (manager.GetNotificationChannel(ChannelId) is not null) return;
        manager.CreateNotificationChannel(new NotificationChannel(ChannelId, "Прокси", NotificationImportance.Low)
        {
            Description = "Состояние прокси TgwsOne",
        });
    }
}
