using Android.App;
using Android.Content;
using Android.Content.PM;
using Android.Graphics;
using Android.OS;
using Android.Views;
using Android.Widget;
using TgwsOne.Mobile;

namespace TgwsOne.Android;

/// <summary>
/// Debug screen. The real UI (E5.2) lives in TgwsOne.Mobile as Avalonia views; this activity is deliberately a
/// plain Android layout so E1.3 can exercise the engine and the service without pulling a theme package in.
/// </summary>
[Activity(
    Name = "io.github.sunsunflowerdeb.tgwsone.MainActivity",
    MainLauncher = true,
    Exported = true,
    Label = "TgwsOne",
    Theme = "@android:style/Theme.Material.Light.NoActionBar")]
public sealed class MainActivity : Activity
{
    private const int NotificationPermissionRequest = 1;
    private const int MaxLogLines = 200;

    private readonly List<string> _lines = [];
    private TextView _state = null!;
    private TextView _link = null!;
    private TextView _log = null!;
    private ScrollView _logScroll = null!;
    private Button _start = null!;
    private Button _stop = null!;
    private Button _open = null!;

    protected override void OnCreate(Bundle? savedInstanceState)
    {
        base.OnCreate(savedInstanceState);
        SetContentView(BuildView());
        EngineRuntime.Changed += OnRuntimeChanged;
        EngineRuntime.Line += OnEngineLine;
        LoadLogTail();
        Render();
        RequestNotificationPermission();
    }

    protected override void OnDestroy()
    {
        EngineRuntime.Changed -= OnRuntimeChanged;
        EngineRuntime.Line -= OnEngineLine;
        base.OnDestroy();
    }

    private View BuildView()
    {
        var root = new LinearLayout(this)
        {
            Orientation = Orientation.Vertical,
        };
        root.SetPadding(Dp(16), Dp(16), Dp(16), Dp(16));

        var title = new TextView(this) { Text = "TgwsOne — отладка" };
        title.SetTextSize(global::Android.Util.ComplexUnitType.Sp, 22);
        title.SetTypeface(Typeface.Default, TypefaceStyle.Bold);
        root.AddView(title);

        _state = new TextView(this);
        _state.SetTextSize(global::Android.Util.ComplexUnitType.Sp, 16);
        _state.SetPadding(0, Dp(8), 0, Dp(8));
        root.AddView(_state);

        _link = new TextView(this);
        _link.SetTextIsSelectable(true);
        _link.SetTextSize(global::Android.Util.ComplexUnitType.Sp, 12);
        _link.SetTextColor(Color.DarkGray);
        _link.SetPadding(0, 0, 0, Dp(8));
        root.AddView(_link);

        var buttons = new LinearLayout(this) { Orientation = Orientation.Horizontal };
        _start = MakeButton("Запустить");
        _stop = MakeButton("Остановить");
        _open = MakeButton("Открыть в Telegram");
        buttons.AddView(_start, new LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WrapContent, 1f));
        buttons.AddView(_stop, new LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WrapContent, 1f));
        buttons.AddView(_open, new LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WrapContent, 1.4f));
        root.AddView(buttons);

        _start.Click += (_, _) => StartProxy();
        _stop.Click += (_, _) => StopProxy();
        _open.Click += (_, _) => OpenTelegram();

        var caption = new TextView(this) { Text = "Журнал" };
        caption.SetTextSize(global::Android.Util.ComplexUnitType.Sp, 14);
        caption.SetPadding(0, Dp(16), 0, Dp(4));
        root.AddView(caption);

        _log = new TextView(this);
        _log.SetTextSize(global::Android.Util.ComplexUnitType.Sp, 10);
        _log.SetTypeface(Typeface.Create("monospace", TypefaceStyle.Normal), TypefaceStyle.Normal);
        _log.SetTextIsSelectable(true);

        _logScroll = new ScrollView(this);
        _logScroll.AddView(_log);
        root.AddView(_logScroll, new LinearLayout.LayoutParams(
            ViewGroup.LayoutParams.MatchParent, 0, 1f));

        return root;
    }

    private Button MakeButton(string text)
    {
        var button = new Button(this) { Text = text };
        button.SetTextSize(global::Android.Util.ComplexUnitType.Sp, 12);
        return button;
    }

    private void StartProxy()
    {
        try
        {
            ProxyService.Start(this);
            _state.Text = "Запускаю…";
        }
        catch (Exception ex)
        {
            _state.Text = "Не удалось запустить службу: " + ex.Message;
        }
    }

    private void StopProxy()
    {
        try
        {
            ProxyService.Stop(this);
        }
        catch (Exception ex)
        {
            _state.Text = "Не удалось остановить службу: " + ex.Message;
        }
    }

    private void OpenTelegram()
    {
        string? link = EngineRuntime.Link;
        if (string.IsNullOrEmpty(link))
        {
            Toast.MakeText(this, "Сначала запустите движок", ToastLength.Short)?.Show();
            return;
        }
        try
        {
            StartActivity(new Intent(Intent.ActionView, global::Android.Net.Uri.Parse(link)));
        }
        catch (ActivityNotFoundException)
        {
            Toast.MakeText(this, "Telegram не установлен", ToastLength.Long)?.Show();
        }
    }

    private void OnRuntimeChanged() => RunOnUiThread(Render);

    private void OnEngineLine(string line) => RunOnUiThread(() => AppendLine(line));

    private void Render()
    {
        bool running = EngineRuntime.IsRunning;
        _state.Text = running
            ? "Движок работает"
            : EngineRuntime.Error is { Length: > 0 } error ? "Ошибка: " + error : "Движок остановлен";
        _link.Text = EngineRuntime.Link ?? "";
        _start.Enabled = !running;
        _stop.Enabled = running;
        _open.Enabled = EngineRuntime.Link is { Length: > 0 };
    }

    private void LoadLogTail()
    {
        try
        {
            string path = MobileStorage.For(FilesDir!.AbsolutePath).ProxyLogPath;
            if (!File.Exists(path)) return;
            foreach (string line in File.ReadLines(path).TakeLast(MaxLogLines))
                _lines.Add(line);
            _log.Text = string.Join('\n', _lines);
            _logScroll.FullScroll(FocusSearchDirection.Down);
        }
        catch (Exception)
        {
            // A missing or unreadable earlier journal is not a reason to fail the debug screen.
        }
    }

    private void AppendLine(string line)
    {
        _lines.Add(line);
        if (_lines.Count > MaxLogLines) _lines.RemoveAt(0);
        _log.Text = string.Join('\n', _lines);
        _logScroll.FullScroll(FocusSearchDirection.Down);
    }

    private void RequestNotificationPermission()
    {
        if (!OperatingSystem.IsAndroidVersionAtLeast(33)) return;
        if (CheckSelfPermission(global::Android.Manifest.Permission.PostNotifications) == Permission.Granted) return;
        RequestPermissions([global::Android.Manifest.Permission.PostNotifications], NotificationPermissionRequest);
    }

    private int Dp(int value) => (int)(value * Resources!.DisplayMetrics!.Density);
}
