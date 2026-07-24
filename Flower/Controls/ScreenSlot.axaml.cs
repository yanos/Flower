using Avalonia;
using Avalonia.Controls;
using Avalonia.Threading;

using Flower.ViewModels.Mobile;

namespace Flower.Controls;

// One screen's own materialized content plus its sliding header - see the
// XAML's own doc comment for why this exists. Frame carries every piece of
// per-screen header state (title/search-visibility/create-playlist/
// download-all gating); genuinely live/global state (SearchQuery, the
// commands, Main.CanForceSync/IsBulkDownloading) binds straight through to
// DataContext instead, which ScreenStackPanel sets to the shared VM on every
// slot it builds.
public partial class ScreenSlot : UserControl
{
    public static readonly StyledProperty<MobileNavigationFrame?> FrameProperty =
        AvaloniaProperty.Register<ScreenSlot, MobileNavigationFrame?>(nameof(Frame));

    public MobileNavigationFrame? Frame
    {
        get => GetValue(FrameProperty);
        set => SetValue(FrameProperty, value);
    }

    public ScreenSlot()
    {
        InitializeComponent();
    }

    // Inserts the wrapped screen control into this slot's content area.
    // Defensive detach first - ScreenControlFactory's LRU cache can hand the
    // same raw Control back out through a brand-new ScreenSlot wrapper later
    // (e.g. revisiting the same album), and Avalonia throws if a Control
    // still has a prior visual parent.
    public void SetContent(Control content)
    {
        if (content.Parent is ContentControl oldHost && !ReferenceEquals(oldHost, ContentHost))
            oldHost.Content = null;
        ContentHost.Content = content;
    }

    public void FocusSearchBox() => Dispatcher.UIThread.Post(() => SearchTabBox.Focus());
}
