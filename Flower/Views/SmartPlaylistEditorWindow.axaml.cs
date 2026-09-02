using Avalonia.Controls;
using Avalonia.Interactivity;

using Flower.ViewModels;

namespace Flower.Views;

// The rule editor. Everything it decides lives in
// SmartPlaylistEditorViewModel; this is the four clicks that reach it.
//
// Modal (ShowDialog), unlike the Equalizer and Log windows: those are things
// you leave open while the music plays, and this one has an OK button whose
// whole meaning is that the playlist is not yet what it says it is.
public partial class SmartPlaylistEditorWindow : Window
{
    // Satisfies Avalonia's runtime-XAML-loader/previewer check (AVLN3001) -
    // never called directly; the real constructor below is what's used.
    public SmartPlaylistEditorWindow() => InitializeComponent();

    public SmartPlaylistEditorWindow(SmartPlaylistEditorViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;
    }

    private SmartPlaylistEditorViewModel? ViewModel => DataContext as SmartPlaylistEditorViewModel;

    // Stays open on a rejected save, with the reason in the footer - the rules
    // are still on screen to fix, which is the whole point of not validating
    // them into a message box.
    private void Ok_Click(object? sender, RoutedEventArgs e)
    {
        if (ViewModel?.Save() == true)
            Close(true);
    }

    private void Cancel_Click(object? sender, RoutedEventArgs e)
    {
        ViewModel?.Cancel();
        Close(false);
    }

    private void AddRow_Click(object? sender, RoutedEventArgs e)
    {
        if (sender is Control { DataContext: SmartConditionRowViewModel row })
            ViewModel?.AddCondition(row);
    }

    private void RemoveRow_Click(object? sender, RoutedEventArgs e)
    {
        if (sender is Control { DataContext: SmartConditionRowViewModel row })
            ViewModel?.RemoveCondition(row);
    }
}
