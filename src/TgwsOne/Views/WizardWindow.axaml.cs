using Avalonia.Controls;
using TgwsOne.ViewModels;

namespace TgwsOne.Views;

internal sealed partial class WizardWindow : Window
{
    public WizardWindow()
    {
        InitializeComponent();
        DataContextChanged += (_, _) =>
        {
            if (DataContext is WizardViewModel vm) vm.CloseRequested += Close;
        };
        // The first step is the connection itself: it starts as the window appears.
        Opened += async (_, _) =>
        {
            if (DataContext is WizardViewModel vm) await vm.StartCommand.ExecuteAsync(null);
        };
    }

    // The cross and Esc end the wizard too: it has had its chance, and is not shown again.
    protected override void OnClosed(EventArgs e)
    {
        base.OnClosed(e);
        (DataContext as WizardViewModel)?.Dismiss();
    }
}
