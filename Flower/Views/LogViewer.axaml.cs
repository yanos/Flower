using System;
using System.Collections.Generic;

using Avalonia;
using Avalonia.Controls;
using Avalonia.Threading;

using Flower.ViewModels;

namespace Flower.Views;

// The log pane itself - the filter header plus the AvaloniaEdit document -
// wherever a log is being read: the app's own Log window (this device's live
// log) and the Logs tab of a server's settings (that server's log, and the
// snapshots its devices pushed). Both hand it a LogViewerViewModel as
// DataContext and it behaves identically.
//
// TextEditor.Text/AppendText are plain CLR properties rather than bindable
// AvaloniaProperties, so the document is driven from the ViewModel's
// LinesReset/LinesAppended events here instead of from XAML.
public partial class LogViewer : UserControl
{
    // The ViewModel currently subscribed to, which is not simply DataContext:
    // this control is subscribed only while it is actually on screen. Without
    // that, every reopened Log window would leave its editor attached to the
    // same singleton LogViewModel, and each one would keep being appended to
    // for the life of the app.
    private LogViewerViewModel? _subscribed;
    private bool _isOnScreen;

    public LogViewer() => InitializeComponent();

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        _isOnScreen = true;
        Sync();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        _isOnScreen = false;
        Sync();
    }

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        Sync();
    }

    private void Sync()
    {
        var target = _isOnScreen ? DataContext as LogViewerViewModel : null;
        if (ReferenceEquals(target, _subscribed))
            return;

        if (_subscribed != null)
        {
            _subscribed.LinesReset -= OnLinesReset;
            _subscribed.LinesAppended -= OnLinesAppended;
        }

        _subscribed = target;
        if (_subscribed == null)
        {
            LogTextEditor.Text = "";
            return;
        }

        _subscribed.LinesReset += OnLinesReset;
        _subscribed.LinesAppended += OnLinesAppended;

        // Whatever it already holds: this control comes on screen long after
        // the ViewModel loaded its first log, so waiting for the next event
        // would mean an empty pane until something changed.
        OnLinesReset(_subscribed, true);
    }

    // LinesReset is a discrete, deliberate event (a different log loaded, or a
    // filter/level change) - grew (see LogViewerViewModel.LinesReset's own doc
    // comment) already means "the underlying log actually has something new,"
    // so this always jumps to the bottom when it does, regardless of where the
    // view was scrolled beforehand - unlike LinesAppended below, this is not a
    // continuous stream the user could already be reading somewhere else in.
    private void OnLinesReset(object? sender, bool grew)
    {
        LogTextEditor.Text = string.Join(Environment.NewLine, _subscribed?.DisplayLines ?? []);
        if (grew)
            ScrollToEndAfterLayout();
    }

    // Unlike LinesReset, this fires continuously while a live log is on screen
    // and logging keeps happening - only follows the tail if the view was
    // already scrolled all the way to the bottom before this batch arrived (the
    // same "stick to bottom" convention a terminal or browser console uses), so
    // reading something further up is never interrupted by new lines landing at
    // the end.
    private void OnLinesAppended(object? sender, IReadOnlyList<string> lines)
    {
        var wasAtBottom = IsScrolledToBottom();
        LogTextEditor.AppendText(string.Join(Environment.NewLine, lines) + Environment.NewLine);
        if (wasAtBottom)
            ScrollToEndAfterLayout();
    }

    private bool IsScrolledToBottom()
    {
        const double epsilon = 2.0; // sub-pixel/rounding tolerance
        return LogTextEditor.VerticalOffset >= LogTextEditor.ExtentHeight - LogTextEditor.ViewportHeight - epsilon;
    }

    // TextEditor.ScrollToEnd() scrolls to the literal maximum scroll offset,
    // which text editors commonly extend past the last line on purpose (so
    // it can be positioned anywhere in the viewport, not pinned to the
    // bottom) - wrong for "keep the latest line visible," and it also moves
    // the horizontal offset. ScrollToLine(line) (TextEditor.ScrollTo with
    // column <= 0) scrolls only the vertical axis - confirmed directly
    // against the AvaloniaEdit source: its horizontal-offset branch is
    // gated on column > 0, so passing no column leaves HorizontalOffset
    // completely untouched, which is what "never scroll horizontally" needs.
    private void ScrollToEndAfterLayout() =>
        Dispatcher.UIThread.Post(() => LogTextEditor.ScrollToLine(LogTextEditor.Document.LineCount), DispatcherPriority.Background);

    private void CopyMenuItem_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e) => LogTextEditor.Copy();
}
