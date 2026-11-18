using System;

using Avalonia;
using Avalonia.Controls;

using Flower.Services;

namespace Flower.Controls;

// Scrolls an album just opened under a row of covers (AlbumExpansionCard) so
// its songs show whole, with Gap to spare under them - but never so far that
// its cover leaves the top of the page: an album with more songs than the
// page holds stops with its cover at the top and the rest a scroll away.
// Nothing moves when the songs already fit where they are, and a cover partly
// above the top is brought down into view. The Albums grid's (AlbumGridView)
// and Home's (HomeView).
//
// Worked out from where the songs will end rather than waiting for them to
// finish opening (AlbumExpansionCard.ExpandedBottom), so the scroll runs
// alongside the expansion. A card closing above the one opening takes what it
// still holds away from everything under it as it goes - shift - and the
// scroll gives that back over the same time with the same easing, so the
// tile clicked holds still instead of sliding up under the pointer.
public static class ExpansionReveal
{
    // Room left between an opened album's songs and the foot of the page -
    // the player bar, when the playback controls are at the bottom.
    private const double Gap = 24;

    // As long as the expansion's own Reveal transition (AlbumExpansionCard
    // .axaml), with the same easing.
    private static readonly TimeSpan Duration = TimeSpan.FromMilliseconds(220);

    // How much longer the reveal keeps after its target once its own time is
    // up: the expansion may finish a frame after it, and until it does the
    // scroller cannot reach an offset past the end of what it holds.
    private static readonly TimeSpan Settle = TimeSpan.FromMilliseconds(150);

    // row: what holds the covers and the card; expandedBottom: where the card
    // will end, from the top of row. Null when there is nothing to scroll.
    public static IDisposable? Start(ScrollViewer scroller, Visual row, double expandedBottom, double shift)
    {
        if (row.TranslatePoint(default, scroller) is not { } at)
            return null;

        // Where the row's top will be in the page once the card closing above
        // it, if any, has closed; where the page would have to sit for the
        // tile to stay just where it was clicked; and from there, the least
        // scroll that shows the songs whole, but not past the cover.
        var from = scroller.Offset;
        var top = from.Y + at.Y - shift;
        var bottom = top + expandedBottom + Gap;
        var held = top - at.Y;
        var target = Math.Max(0, Math.Min(Math.Max(held, bottom - scroller.Viewport.Height), top));
        var by = target - from.Y;
        if (Math.Abs(by) < 0.5)
            return null;

        IDisposable? reveal = null;
        reveal = AnimationClock.Current.Subscribe(elapsed =>
        {
            var t = Math.Min(1.0, elapsed / Duration);
            scroller.Offset = new Vector(from.X, from.Y + by * EaseOut(t));
            if (t < 1.0 || (Math.Abs(scroller.Offset.Y - target) >= 0.5 && elapsed < Duration + Settle))
                return;
            reveal!.Dispose();
        });
        return reveal;
    }

    // The same curve as the expansion's CubicEaseOut.
    private static double EaseOut(double t) => 1 - Math.Pow(1 - t, 3);
}
