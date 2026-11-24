using System;

using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

using Material.Icons;
using Material.Icons.Avalonia;

namespace Flower.Controls;

/// <summary>
/// TextInput's clear button, for a box that is not a TextInput: Track Info's
/// AutoCompleteBoxes, in their InnerRightContent. Shown while its command can
/// run - for those boxes, while they show "Multiple values" and can be emptied
/// for every song (TrackInfoField.Clear). A box with text of its own clears it
/// the ordinary way, by selecting it and deleting.
/// </summary>
public class MixedValueClearButton : Button
{
    protected override Type StyleKeyOverride => typeof(Button);

    public MixedValueClearButton()
    {
        Background = Brushes.Transparent;
        BorderThickness = default;
        Padding = new Thickness(4);
        // Like TextInput's: a box that commits what it holds when it loses
        // focus would commit on the way to being cleared.
        Focusable = false;
        Content = new MaterialIcon { Kind = MaterialIconKind.CloseCircle, Width = 16, Height = 16, Opacity = 0.6 };
        IsVisible = false;
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == CommandProperty)
        {
            if (change.GetOldValue<System.Windows.Input.ICommand?>() is { } old)
                old.CanExecuteChanged -= OnCanExecuteChanged;
            if (change.GetNewValue<System.Windows.Input.ICommand?>() is { } now)
                now.CanExecuteChanged += OnCanExecuteChanged;
            OnCanExecuteChanged(this, EventArgs.Empty);
        }
    }

    private void OnCanExecuteChanged(object? sender, EventArgs e) =>
        IsVisible = Command?.CanExecute(CommandParameter) == true;
}
