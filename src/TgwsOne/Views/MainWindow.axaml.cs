using Avalonia.Controls;

namespace TgwsOne.Views;

internal sealed partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();

        // The XAML size is for a roomy screen; a smaller one gets what fits (no screen to ask, as in a headless test: it stays).
        if (Screens.Primary is { Scaling: > 0 } screen)
            (Width, Height) = WindowSizing.Fit(screen.WorkingArea.Width / screen.Scaling, screen.WorkingArea.Height / screen.Scaling);
    }
}
