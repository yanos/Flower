using System;
using System.Collections.Generic;

using Avalonia.Controls;
using Avalonia.Threading;

using Flower.ViewModels;

namespace Flower.Views;

public partial class LogWindow : Window
{
    private readonly LogViewModel _viewModel;

    public LogWindow(LogViewModel viewModel)
    {
        InitializeComponent();

        _viewModel = viewModel;
        DataContext = viewModel;

        // Wired before RefreshSidebarItems() below, which synchronously
        // triggers LoadSelection -> RenderLines -> LinesReset - subscribing
        // first is what makes the initial content actually show up instead
        // of firing into nothing.
        _viewModel.LinesReset += OnLinesReset;
        _viewModel.LinesAppended += OnLinesAppended;

        // Picks up any IsServer/trusted-peer change since this ViewModel
        // (a DI singleton) was last used, without needing a live
        // cross-ViewModel notification - see LogViewModel.RefreshSidebarItems.
        _viewModel.RefreshSidebarItems();

        // Deliberately NOT NativeMenuHelper.InheritFromMainWindow(this) -
        // every other caller of that helper (Settings, Track Info, ...) is
        // opened via ShowDialog, so it only ever shares MainWindow's
        // NativeMenu object with one other window at a time, sequentially.
        // This window is non-modal (.Show()) and stays open alongside
        // MainWindow, so the two would hold the exact same NativeMenu
        // instance simultaneously - confirmed responsible for the app's
        // whole menu bar (Library/View, even the Flower app menu) breaking
        // after this window had been open, most likely from Avalonia's
        // macOS native menu bridge not expecting one NativeMenu object to be
        // attached to two live windows at once. The bare "Avalonia
        // Application" fallback menu while this window has focus is a small
        // price for not risking MainWindow's own menu again.
    }

    // LinesReset is a discrete, deliberate event (switching sidebar
    // selection, a filter/level change, or a remote snapshot refreshing) -
    // grew (see LogViewModel.LinesReset's own doc comment) already means
    // "the underlying log actually has something new," so this always jumps
    // to the bottom when it does, regardless of where the view was scrolled
    // beforehand - unlike LinesAppended below, this is not a continuous
    // stream the user could already be reading somewhere else in.
    private void OnLinesReset(object? sender, bool grew)
    {
        LogTextEditor.Text = string.Join(Environment.NewLine, _viewModel.DisplayLines);
        if (grew)
            ScrollToEndAfterLayout();
    }

    // Unlike LinesReset, this fires continuously while "This Device" is
    // selected and logging keeps happening - only follows the tail if the
    // view was already scrolled all the way to the bottom before this batch
    // arrived (the same "stick to bottom" convention a terminal or browser
    // console uses), so reading something further up is never interrupted
    // by new lines landing at the end.
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
