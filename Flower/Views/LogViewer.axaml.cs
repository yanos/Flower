using System;
using System.Collections.Generic;
using System.IO;

using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Platform.Storage;
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
// LinesReset/LinesAppended/LastLineReplaced events here instead of from
// XAML. The document is always the lines joined by line breaks, with none
// after the last, so the last line - the one a repeat rewrites - runs from
// _lastLineStart to the end.
public partial class LogViewer : UserControl
{
    // The ViewModel currently subscribed to, which is not simply DataContext:
    // this control is subscribed only while it is actually on screen. Without
    // that, every reopened Log window would leave its editor attached to the
    // same singleton LogViewModel, and each one would keep being appended to
    // for the life of the app.
    private LogViewerViewModel? _subscribed;
    private bool _isOnScreen;
    private int _lastLineStart;
    private readonly LogLevelColorizer _colorizer = new();

    public LogViewer()
    {
        InitializeComponent();
        LogTextEditor.TextArea.TextView.LineTransformers.Add(_colorizer);
        ActualThemeVariantChanged += (_, _) => ApplyColours();
    }

    // The level tags' tints and the other colours, from the theme in use -
    // again whenever it changes, since the editor draws them itself.
    private void ApplyColours()
    {
        IBrush Brush(string key) =>
            this.TryFindResource(key, ActualThemeVariant, out var value) && value is IBrush brush ? brush : Brushes.Transparent;

        _colorizer.Colours = new LogLevelColorizer.Palette(
            Brush("AppTextMutedBrush"), Brush("AppTextValueBrush"),
            Brush("AppLogDebugBrush"), Brush("AppLogDebugTextBrush"),
            Brush("AppLogInfoBrush"), Brush("AppLogInfoTextBrush"),
            Brush("AppLogWarningBrush"), Brush("AppLogWarningTextBrush"),
            Brush("AppLogErrorBrush"), Brush("AppLogErrorTextBrush"));
        // An address in a line is drawn as a link, in the editor's own blue
        // unless told otherwise - which on the dark theme could not be read.
        LogTextEditor.TextArea.TextView.LinkTextForegroundBrush = Brush("AppTextPrimaryBrush");
        LogTextEditor.TextArea.TextView.Redraw();
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        _isOnScreen = true;
        ApplyColours();
        // A file to save to is not something every host can offer - the
        // browser's, for one.
        ExportButton.IsVisible = TopLevel.GetTopLevel(this)?.StorageProvider.CanSave == true;
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
            _subscribed.LastLineReplaced -= OnLastLineReplaced;
        }

        _subscribed = target;
        if (_subscribed == null)
        {
            LogTextEditor.Text = "";
            return;
        }

        _subscribed.LinesReset += OnLinesReset;
        _subscribed.LinesAppended += OnLinesAppended;
        _subscribed.LastLineReplaced += OnLastLineReplaced;

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
        var lines = _subscribed?.DisplayLines ?? [];
        LogTextEditor.Text = string.Join(Environment.NewLine, lines);
        _lastLineStart = lines.Count == 0 ? 0 : LogTextEditor.Document.TextLength - lines[^1].Length;
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
        var before = LogTextEditor.Document.TextLength;
        var text = (before > 0 ? Environment.NewLine : "") + string.Join(Environment.NewLine, lines);
        LogTextEditor.AppendText(text);
        _lastLineStart = before + text.Length - lines[^1].Length;
        if (wasAtBottom)
            ScrollToEndAfterLayout();
    }

    // The last line, which a repeat just added to (IsGroupingRepeats),
    // rewritten where it is.
    private void OnLastLineReplaced(object? sender, string line)
    {
        var document = LogTextEditor.Document;
        var start = Math.Min(_lastLineStart, document.TextLength);
        document.Replace(start, document.TextLength - start, line);
    }

    private void SmallerText_Click(object? sender, RoutedEventArgs e) => _subscribed?.SmallerText();

    private void LargerText_Click(object? sender, RoutedEventArgs e) => _subscribed?.LargerText();

    // Every line shown, as shown, whatever is selected.
    private void CopyButton_Click(object? sender, RoutedEventArgs e) => CopyAll();

    // The whole log for this source, ungrouped and unfiltered, to a file
    // picked for it.
    private async void ExportButton_Click(object? sender, RoutedEventArgs e)
    {
        if (_subscribed is not { } viewer || TopLevel.GetTopLevel(this)?.StorageProvider is not { CanSave: true } storage)
            return;

        var file = await storage.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = "Export Log",
            SuggestedFileName = $"Flower log {DateTime.Now:yyyy-MM-dd HHmm}.txt",
            DefaultExtension = "txt",
            FileTypeChoices = [FilePickerFileTypes.TextPlain],
        });
        if (file == null)
            return;

        await using var stream = await file.OpenWriteAsync();
        await using var writer = new StreamWriter(stream);
        await writer.WriteAsync(viewer.ExportText);
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

    // Copies the whole document when nothing is selected, rather than doing
    // nothing - which is what TextEditor.Copy() alone does, and what made the
    // one line most worth copying uncopyable. A failure this pane reports is a
    // single line of placeholder text (see LogViewerViewModel.ShowPlaceholder),
    // and reaching for Copy on it is not a request for the empty selection.
    private void CopyMenuItem_Click(object? sender, RoutedEventArgs e)
    {
        if (LogTextEditor.SelectionLength > 0)
        {
            LogTextEditor.Copy();
            return;
        }

        CopyAll();
    }

    // Through the editor's own selection rather than the clipboard directly:
    // AvaloniaEdit's Copy is what knows how to put text on the clipboard on
    // every host it runs on, the browser included.
    private void CopyAll()
    {
        LogTextEditor.SelectAll();
        LogTextEditor.Copy();
        LogTextEditor.SelectionLength = 0;
    }
}
