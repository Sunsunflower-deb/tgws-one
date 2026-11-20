using Avalonia.Headless.XUnit;
using TgwsOne.Views;

namespace TgwsOne.Tests;

public class WindowSizingTests
{
    [Fact]
    public void ARoomyScreen_GetsTheDefaultSize()
    {
        // 1080p minus a 40 px taskbar.
        Assert.Equal((880d, 730d), WindowSizing.Fit(1920, 1040));
        Assert.Equal((880d, 730d), WindowSizing.Fit(1920, 1080));
    }

    [Fact]
    public void ALaptopScreen_GetsAWindowThatFitsInsideTheWorkingArea()
    {
        (double width, double height) = WindowSizing.Fit(1366, 728);

        Assert.Equal(880, width);                               // wide enough already
        Assert.True(height <= 728 - WindowSizing.Margin);       // room left for the title bar and frame
        Assert.True(height < WindowSizing.DefaultHeight);
        Assert.True(height >= WindowSizing.MinHeight);
    }

    [Theory]
    [InlineData(1366, 728)]
    [InlineData(1280, 720)]
    [InlineData(1024, 768)]
    [InlineData(800, 600)]
    public void TheWindow_NeverExceedsTheWorkingArea_WhenThatIsPossible(double workWidth, double workHeight)
    {
        (double width, double height) = WindowSizing.Fit(workWidth, workHeight);

        Assert.True(width <= workWidth);
        Assert.True(height <= workHeight);
        Assert.True(width >= WindowSizing.MinWidth);
        Assert.True(height >= WindowSizing.MinHeight);
    }

    [Fact]
    public void AScreenTooSmallForTheMinimum_StillGetsTheMinimum()
    {
        Assert.Equal((WindowSizing.MinWidth, WindowSizing.MinHeight), WindowSizing.Fit(640, 400));
    }

    [Fact]
    public void ADenseScreen_IsMeasuredInDeviceIndependentPixels()
    {
        // The caller divides the pixel size by the scale: 3840×2160 at 200 % is 1920×1080 of the window's units.
        Assert.Equal((880d, 730d), WindowSizing.Fit(3840 / 2.0, 2160 / 2.0));
        // 2560×1440 at 200 % is 1280×720: not enough height for the default.
        Assert.True(WindowSizing.Fit(2560 / 2.0, 1440 / 2.0).Height < 730);
    }

    [AvaloniaFact]
    public void TheXamlSizeAndTheCodeDefaultsAgree()
    {
        var window = new MainWindow();

        Assert.Equal(WindowSizing.MinWidth, window.MinWidth);
        Assert.Equal(WindowSizing.MinHeight, window.MinHeight);
        Assert.Equal(WindowSizing.DefaultWidth, window.Width);
        Assert.Equal(WindowSizing.DefaultHeight, window.Height);
    }
}
