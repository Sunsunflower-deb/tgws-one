using Avalonia.Controls;
using Avalonia.Interactivity;

namespace TgwsOne.Views;

/// <summary>A question with two answers. Closes with <c>true</c> for the accepting button, <c>false</c> for the other one, Esc or the close button.</summary>
internal sealed partial class ConfirmDialog : Window
{
    public ConfirmDialog()
    {
        InitializeComponent();
    }

    public ConfirmDialog(string title, string message, string accept, string cancel) : this()
    {
        Title = title;
        MessageText.Text = message;
        AcceptButton.Content = accept;
        CancelButton.Content = cancel;
    }

    private void OnAccept(object? sender, RoutedEventArgs e) => Close(true);

    private void OnCancel(object? sender, RoutedEventArgs e) => Close(false);
}
