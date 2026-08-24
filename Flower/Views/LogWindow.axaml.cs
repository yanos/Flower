using Avalonia.Controls;

using Flower.ViewModels;

namespace Flower.Views;

// View > Log... - a window around LogViewer, showing this device's own live
// log. Everything about rendering a log lives in that control; all this adds
// is the window, and the one thing that is genuinely per-window (see the
// NativeMenu note below).
public partial class LogWindow : Window
{
    // Satisfies Avalonia's runtime-XAML-loader/previewer check (AVLN3001) -
    // never called directly; the real constructor below is what's actually used.
    public LogWindow() => InitializeComponent();

    public LogWindow(LogViewModel viewModel)
    {
        InitializeComponent();

        // Re-reads the buffer before the viewer attaches: this ViewModel is a
        // DI singleton, so on every reopen after the first its events have long
        // since fired and there would be nothing for the fresh control to paint
        // from.
        viewModel.Reload();
        DataContext = viewModel;

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
}
