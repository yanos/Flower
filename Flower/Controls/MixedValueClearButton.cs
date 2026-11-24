using System;
using System.Windows.Input;

using Avalonia;

namespace Flower.Controls;

/// <summary>
/// TextInput's clear button, for a box that is not a TextInput: Track Info's
/// AutoCompleteBoxes, in their InnerRightContent. Shown while its command can
/// run - for those boxes, while they show "Multiple values" and can be emptied
/// for every song (TrackInfoField.Clear). A box with text of its own clears it
/// the ordinary way, by selecting it and deleting.
/// </summary>
public class MixedValueClearButton : ClearButton
{
    public MixedValueClearButton() => IsVisible = false;

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == CommandProperty)
        {
            if (change.GetOldValue<ICommand?>() is { } old)
                old.CanExecuteChanged -= OnCanExecuteChanged;
            if (change.GetNewValue<ICommand?>() is { } now)
                now.CanExecuteChanged += OnCanExecuteChanged;
            OnCanExecuteChanged(this, EventArgs.Empty);
        }
    }

    private void OnCanExecuteChanged(object? sender, EventArgs e) =>
        IsVisible = Command?.CanExecute(CommandParameter) == true;
}
