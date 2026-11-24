using System;

using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

using Material.Icons;
using Material.Icons.Avalonia;

namespace Flower.Controls;

/// <summary>
/// The small clear button inside a text box: TextInput's, and Track Info's
/// (MixedValueClearButton).
/// </summary>
public class ClearButton : Button
{
    protected override Type StyleKeyOverride => typeof(Button);

    public ClearButton()
    {
        Background = Brushes.Transparent;
        BorderThickness = default;
        Padding = new Thickness(4);
        // Nothing about this button is a place to type, and taking focus
        // would be a real change of behaviour rather than a cosmetic one:
        // a box that commits what it holds when it loses focus (the new
        // playlist's name box, Track Info's) would commit on the way to
        // being cleared.
        Focusable = false;
        Content = new MaterialIcon { Kind = MaterialIconKind.CloseCircle, Width = 16, Height = 16, Opacity = 0.6 };
    }
}
