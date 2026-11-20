using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Media.Imaging;
using Avalonia.Data.Converters;

namespace TgwsOne.Views;

internal static class QrConverters
{
    /// <summary>The QR code's PNG as a picture.</summary>
    public static readonly FuncValueConverter<byte[]?, Bitmap?> PngToBitmap = new(png => png is null ? null : new Bitmap(new MemoryStream(png)));
}

internal sealed partial class QrDialog : Window
{
    public QrDialog()
    {
        InitializeComponent();
    }

    private void OnClose(object? sender, RoutedEventArgs e) => Close();
}
