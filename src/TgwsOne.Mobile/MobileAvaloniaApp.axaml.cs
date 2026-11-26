using Avalonia;
using Avalonia.Markup.Xaml;
using Avalonia.Styling;

namespace TgwsOne.Mobile;

/// <summary>
/// The Avalonia application of the phone build: the Fluent theme and the colours the pages share. The Android
/// project only starts it (<c>AvaloniaAndroidApplication</c>) and hands it to <c>AvaloniaMainActivity</c>.
/// </summary>
public sealed class MobileAvaloniaApp : Avalonia.Application
{
    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        // Follow the system's light/dark choice; the Android platform reports it on every configuration change.
        RequestedThemeVariant = ThemeVariant.Default;
        base.OnFrameworkInitializationCompleted();
    }
}
