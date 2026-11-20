using Avalonia.Controls;
using TgwsOne.ViewModels;

namespace TgwsOne.Views;

internal sealed partial class CustomStrategyDialog : Window
{
    public CustomStrategyDialog()
    {
        InitializeComponent();
        DataContextChanged += (_, _) =>
        {
            if (DataContext is CustomStrategyViewModel vm) vm.CloseRequested += Close;
        };
    }
}
