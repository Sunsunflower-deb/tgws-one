using Avalonia.Controls;
using TgwsOne.ViewModels;

namespace TgwsOne.Views;

/// <summary>
/// The finder's window. Closing it (the cross, Esc, Alt+F4) never leaves the bypass on whatever strategy the run was
/// trying: the first close attempt stops and restores, and the window closes once that is done.
/// </summary>
internal sealed partial class FinderDialog : Window
{
    private bool _closeCalled;

    public FinderDialog()
    {
        InitializeComponent();
        DataContextChanged += (_, _) =>
        {
            if (DataContext is FinderViewModel vm) vm.CloseRequested += CloseOnce;
        };
    }

    // The view-model asks for the close when it is done, and so does the continuation of a close attempt that had to wait for it.
    private void CloseOnce()
    {
        if (_closeCalled) return;
        _closeCalled = true;
        Close();
    }

    protected override async void OnClosing(WindowClosingEventArgs e)
    {
        base.OnClosing(e);
        if (_closeCalled || DataContext is not FinderViewModel { CanCloseNow: false } vm) return;

        e.Cancel = true;
        await vm.ResolveAsync();
        if (vm.CanCloseNow) CloseOnce();
    }
}
