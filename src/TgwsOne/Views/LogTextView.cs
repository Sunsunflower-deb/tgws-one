using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Media;
using Avalonia.Threading;

namespace TgwsOne.Views;

/// <summary>
/// A log's text: monospace, selectable, scrollable both ways. When it is told to follow the end it keeps the newest line in view,
/// but only as long as the reader is at the end: someone who has scrolled up to read older lines is not pulled back down.
/// </summary>
internal sealed class LogTextView : UserControl
{
    public static readonly StyledProperty<string?> TextProperty = AvaloniaProperty.Register<LogTextView, string?>(nameof(Text));
    public static readonly StyledProperty<bool> FollowEndProperty = AvaloniaProperty.Register<LogTextView, bool>(nameof(FollowEnd), true);

    private const double AtEndSlack = 3;

    private readonly ScrollViewer _scroll;
    private readonly SelectableTextBlock _block;

    public LogTextView()
    {
        _block = new SelectableTextBlock
        {
            FontFamily = new FontFamily("Cascadia Mono, Consolas, Menlo, DejaVu Sans Mono, Liberation Mono, Monospace"),
            FontSize = 12,
            Padding = new Thickness(14),
        };
        _scroll = new ScrollViewer
        {
            HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            Content = _block,
        };
        Content = _scroll;
    }

    public string? Text
    {
        get => GetValue(TextProperty);
        set => SetValue(TextProperty, value);
    }

    public bool FollowEnd
    {
        get => GetValue(FollowEndProperty);
        set => SetValue(FollowEndProperty, value);
    }

    /// <summary>The scrolling part, for tests.</summary>
    internal ScrollViewer Scroller => _scroll;

    private bool IsAtEnd()
        => _scroll.Extent.Height <= _scroll.Viewport.Height + AtEndSlack
           || _scroll.Offset.Y >= _scroll.Extent.Height - _scroll.Viewport.Height - AtEndSlack;

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);

        if (change.Property == TextProperty)
        {
            bool wasAtEnd = IsAtEnd();   // judged before the new text changes the extent
            _block.Text = Text ?? "";
            if (FollowEnd && wasAtEnd) ScrollToEndAfterLayout();
        }
        else if (change.Property == FollowEndProperty && FollowEnd)
        {
            ScrollToEndAfterLayout();
        }
    }

    private void ScrollToEndAfterLayout() => Dispatcher.UIThread.Post(_scroll.ScrollToEnd, DispatcherPriority.Loaded);
}
