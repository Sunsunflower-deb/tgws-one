using Android.App;
using Android.Runtime;
using AndroidLog = Android.Util.Log;

namespace TgwsOne.Android;

[Application(Name = "io.github.sunsunflowerdeb.tgwsone.MainApplication", Label = "TgwsOne")]
public sealed class MainApplication : Application
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
