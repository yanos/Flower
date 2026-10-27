using System;

using Avalonia;
using Avalonia.Controls;

namespace Flower.Controls;

// A vertical StackPanel whose last section sits at the bottom of the room it
// is given rather than straight under the rest: the desktop sidebar's server
// card, at the foot of the column under its heading. From the first child
// marked PinsFromHere on, every child is moved down by whatever height the
// panel has spare; with none spare - a long list of playlists - they follow on
// as in any StackPanel and scroll with the rest. A ListBox's scroller arranges
// its content at least as tall as its viewport, which is where the spare room
// comes from. Still a StackPanel, so the arrow keys move along it the same.
public class BottomPinnedStackPanel : StackPanel
{
    public static readonly AttachedProperty<bool> PinsFromHereProperty =
        AvaloniaProperty.RegisterAttached<BottomPinnedStackPanel, Control, bool>("PinsFromHere");

    public static bool GetPinsFromHere(Control control) => control.GetValue(PinsFromHereProperty);
    public static void SetPinsFromHere(Control control, bool value) => control.SetValue(PinsFromHereProperty, value);

    static BottomPinnedStackPanel()
    {
        AffectsParentArrange<BottomPinnedStackPanel>(PinsFromHereProperty);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        var used = 0.0;
        var visible = 0;
        foreach (var child in Children)
        {
            if (!child.IsVisible)
                continue;
            used += child.DesiredSize.Height;
            visible++;
        }
        used += Math.Max(0, visible - 1) * Spacing;
        var spare = Math.Max(0, finalSize.Height - used);

        var y = 0.0;
        var pinned = false;
        foreach (var child in Children)
        {
            if (!child.IsVisible)
                continue;
            if (!pinned && GetPinsFromHere(child))
            {
                pinned = true;
                y += spare;
            }
            child.Arrange(new Rect(0, y, finalSize.Width, child.DesiredSize.Height));
            y += child.DesiredSize.Height + Spacing;
        }
        return finalSize;
    }
}
