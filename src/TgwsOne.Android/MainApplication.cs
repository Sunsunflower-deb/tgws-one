using Android.App;
using Android.Runtime;
using Avalonia.Android;
using TgwsOne.Mobile;
using AndroidLog = Android.Util.Log;

namespace TgwsOne.Android;

/// <summary>
/// Starts the Avalonia application (the phone pages live in <c>TgwsOne.Mobile</c>) and keeps the process-wide
/// unhandled-exception log. The foreground service and the engine stay outside Avalonia's lifetime.
/// </summary>
[Application(Name = "io.github.sunsunflowerdeb.tgwsone.MainApplication", Label = "TgwsOne")]
public sealed class MainApplication : AvaloniaAndroidApplication<MobileAvaloniaApp>
{
    public MainApplication(IntPtr handle, JniHandleOwnership transfer) : base(handle, transfer) { }

    public override void OnCreate()
    {
        base.OnCreate();
        AndroidLog.Info("TgwsOne", "application started");
        AndroidEnvironment.UnhandledExceptionRaiser += (_, e) =>
            AndroidLog.Error("TgwsOne", "unhandled exception: " + e.Exception);
    }
}
