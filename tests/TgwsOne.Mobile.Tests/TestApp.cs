using Avalonia;
using Avalonia.Headless;
using TgwsOne.Mobile;

[assembly: AvaloniaTestApplication(typeof(TgwsOne.Mobile.Tests.TestAppBuilder))]

namespace TgwsOne.Mobile.Tests;

/// <summary>The phone application (theme and colours) on the headless platform: the pages are laid out, nothing is shown.</summary>
public static class TestAppBuilder
{
    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<MobileAvaloniaApp>().UseHeadless(new AvaloniaHeadlessPlatformOptions());
}
