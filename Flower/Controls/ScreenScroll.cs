using Avalonia;
using Avalonia.Controls;

namespace Flower.Controls;

/// <summary>
/// One thing a screen's scrolling ListBox can show above its first item, after
/// the screen's title - a playlist's own header (see TrackListScreenView).
/// </summary>
/// <remarks>
/// Attached rather than a property on a control of ours, because the scroller
/// in question is a plain ListBox wearing the screenScroll style
/// (MobileMainView.axaml), and its template is the only place with somewhere
/// to put a line before the items. The alternative - a ScrollViewer over a
/// non-virtualizing ItemsControl, which is what the album screen does - would
/// have cost a long playlist its virtualization and the drag handle its list.
/// </remarks>
public static class ScreenScroll
{
    public static readonly AttachedProperty<object?> HeaderProperty =
        AvaloniaProperty.RegisterAttached<ListBox, object?>("Header", typeof(ScreenScroll));

    public static object? GetHeader(ListBox listBox) => listBox.GetValue(HeaderProperty);

    public static void SetHeader(ListBox listBox, object? value) => listBox.SetValue(HeaderProperty, value);
}
