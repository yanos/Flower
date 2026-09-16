using Avalonia;
using Avalonia.Controls;
using Avalonia.Threading;

using Flower.ViewModels.Mobile;

namespace Flower.Controls;

// One screen's own materialized content plus its sliding header - see the
// XAML's own doc comment for why this exists. Frame carries the per-screen
// header state, which is now down to one question (is this the Search tab, and
// so is the box showing); genuinely live/global state - the query itself -
// binds straight through to DataContext instead, which ScreenStackPanel sets
// to the shared VM on every slot it builds.
public partial class ScreenSlot : UserControl
{
    // The header band's height - the XAML's first row. The screen runs up
    // under it, so what scrolls starts this far down (MobileMainView's
    // ScreenScrollInset) and what does not keeps clear of it on its own.
    public const double HeaderHeight = 52;

    // ...plus this much before anything of the screen's own begins. The band
    // is see-through and the back button sits in it, so a title or a first row
    // that started flush against the band's edge started right under that
    // button. Every screen takes both together (MobileMainView's
    // ScreenScrollInset, and the pinned album art in TrackListScreenView,
    // which is the one piece of screen content outside a scroller).
    public const double ContentGap = 12;

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
