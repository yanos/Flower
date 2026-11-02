using Avalonia.Controls;

using Flower.ViewModels;

namespace Flower.Views;

// The Vibe Playlist editor as a desktop dialog. Modal, like the smart
// playlist editor's: the playlist is not what it says until OK.
public sealed class VibePlaylistEditorWindow : Window
{
    public VibePlaylistEditorWindow(VibePlaylistEditorViewModel viewModel, MainViewModel main)
    {
        Title = "Vibe Playlist";
        Width = 760;
        MinWidth = 560;
        Height = 620;
        MinHeight = 420;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;

        var view = new VibePlaylistEditorView(viewModel, main);
        view.Closed += (_, saved) => Close(saved);
        // Closing the window any other way is a cancel.
        Closing += (_, _) =>
        {
            if (!_ended)
                viewModel.Cancel();
        };
        view.Closed += (_, _) => _ended = true;
        Content = view;
    }

    private bool _ended;
}
