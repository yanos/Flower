using System;

using Avalonia;
using Avalonia.Controls;

namespace Flower.Controls;

// The now-playing sheet's body: album art and everything else, stacked when the
// phone is upright and side by side when it is turned. Two children in order -
// the art first, the controls second - and no properties of its own.
//
// It is a panel rather than a couple of Grid.Row/Grid.Column assignments flipped
// from a Bounds handler, because that flip runs inside the layout pass it is
// reacting to: setting a layout property there invalidates the ancestor being
// arranged, which schedules the pass that raises the same notification again.
// Deciding here instead makes the orientation a pure function of the space the
// panel is given, so measure and arrange agree by construction and there is
// nothing to re-enter.
//
// The art is square and sized by the short edge, which is the whole point: in
// landscape the sheet is a couple of hundred pixels tall and a cover that keeps
// its portrait size overflows the row and paints over the seek slider (nothing
// in this sheet clips).
public class NowPlayingBodyPanel : Panel
{
    // Past this much wider than tall, the art moves to the side. Comfortably
    // above 1 so a nearly-square window - a resized desktop, a tablet in a
    // split view - stays in the layout it was already in rather than sitting on
    // the boundary.
    private const double LandscapeAspect = 1.2;

    // The portrait cap the fixed 280px square used to be. Landscape is capped
    // the same way, so a tall-enough sheet never blows the cover up past the
    // size the design was drawn at.
    private const double MaxArtSide = 280;

    // Between the art and the controls, whichever side it is on.
    private const double Gap = 16;

    // Portrait only: the art never takes more than this share of the height, so
    // a short window still leaves the title, seek bar and transport row theirs.
    private const double MaxPortraitArtShare = 0.6;

    protected override Size MeasureOverride(Size availableSize)
    {
        if (Children.Count < 2)
            return base.MeasureOverride(availableSize);

        // An unconstrained pass (a scroll viewer, a sheet measured before it is
        // placed) has no short edge to size the art off, so fall back to the
        // portrait shape at its natural size.
        var width = double.IsInfinity(availableSize.Width) ? MaxArtSide : availableSize.Width;
        var height = double.IsInfinity(availableSize.Height) ? double.PositiveInfinity : availableSize.Height;

        var (art, content) = Slots(new Size(width, height));

        Children[0].Measure(art.Size);
        Children[1].Measure(content.Size);

        if (double.IsInfinity(availableSize.Width) || double.IsInfinity(availableSize.Height))
        {
            var desiredWidth = Math.Max(Children[0].DesiredSize.Width, Children[1].DesiredSize.Width);
            return new Size(desiredWidth, Children[0].DesiredSize.Height + Gap + Children[1].DesiredSize.Height);
        }

        return availableSize;
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        if (Children.Count < 2)
            return base.ArrangeOverride(finalSize);

        var (art, content) = Slots(finalSize);
        Children[0].Arrange(art);
        Children[1].Arrange(content);
        return finalSize;
    }

    // The one place the layout is decided, so measure and arrange cannot disagree.
    private static (Rect Art, Rect Content) Slots(Size size)
    {
        var landscape = !double.IsInfinity(size.Height) && size.Width > size.Height * LandscapeAspect;

        if (landscape)
        {
            var side = Math.Max(0, Math.Min(Math.Min(size.Height, MaxArtSide), size.Width / 2));
            var contentWidth = Math.Max(0, size.Width - side - Gap);

            // Centred on the short edge: the square only fills the height while
            // the sheet is shorter than the cap.
            var top = Math.Max(0, (size.Height - side) / 2);
            return (new Rect(0, top, side, side),
                    new Rect(side + Gap, 0, contentWidth, size.Height));
        }

        var portraitSide = Math.Min(size.Width, MaxArtSide);
        if (!double.IsInfinity(size.Height))
            portraitSide = Math.Min(portraitSide, size.Height * MaxPortraitArtShare);
        portraitSide = Math.Max(0, portraitSide);

        var contentHeight = double.IsInfinity(size.Height)
            ? double.PositiveInfinity
            : Math.Max(0, size.Height - portraitSide - Gap);

        // Centred across the width, the way the fixed square used to be.
        var left = Math.Max(0, (size.Width - portraitSide) / 2);
        return (new Rect(left, 0, portraitSide, portraitSide),
                new Rect(0, portraitSide + Gap, size.Width, contentHeight));
    }
}
