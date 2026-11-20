using Avalonia;
using Avalonia.Headless;
using TgwsOne.Tests;

[assembly: AvaloniaTestApplication(typeof(TestAppBuilder))]

namespace TgwsOne.Tests;

/// <summary>The real <c>App</c> (theme, styles) on the headless platform: windows are created and laid out, nothing is shown on a screen.</summary>
public static class TestAppBuilder
{
    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<App>().UseHeadless(new AvaloniaHeadlessPlatformOptions());
}
