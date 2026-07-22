using System;
using System.Collections.Generic;

using Avalonia.Controls;
using Avalonia.Threading;

using Flower.Services;
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

        NativeMenuHelper.InheritFromMainWindow(this);
    }

    private void OnLinesReset(object? sender, EventArgs e)
    {
        LogTextEditor.Text = string.Join(Environment.NewLine, _viewModel.DisplayLines);
        ScrollToEndAfterLayout();
    }

    private void OnLinesAppended(object? sender, IReadOnlyList<string> lines)
    {
        LogTextEditor.AppendText(string.Join(Environment.NewLine, lines) + Environment.NewLine);
        ScrollToEndAfterLayout();
    }

    // TextEditor.ScrollToEnd() (a thin wrapper over the internal
    // ScrollViewer's own ScrollToEnd) scrolls to the literal maximum scroll
    // offset - confirmed NOT a layout-timing issue (deferring it changed
    // nothing) - which text editors commonly extend past the last line on
    // purpose, so the last line can be positioned anywhere in the viewport
    // rather than pinned to the bottom edge. That's the wrong semantic for
    // "keep the latest line visible": moving the caret to the end of the
    // document and asking the TextArea to bring *it* into view scrolls only
    // the minimum needed to reveal that position, landing the last line at
    // the bottom edge instead of the top.
    private void ScrollToEndAfterLayout() =>
        Dispatcher.UIThread.Post(() =>
        {
            LogTextEditor.CaretOffset = LogTextEditor.Document.TextLength;
            LogTextEditor.TextArea.Caret.BringCaretToView();
        }, DispatcherPriority.Background);

    private void CopyMenuItem_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e) => LogTextEditor.Copy();
}
