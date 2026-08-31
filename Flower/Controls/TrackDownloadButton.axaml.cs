using System;

using Avalonia.Controls;
using Avalonia.Interactivity;

using Flower.ViewModels;

namespace Flower.Controls;

public partial class TrackDownloadButton : UserControl
{
    public TrackDownloadButton()
    {
        InitializeComponent();
        DownloadButton.Click += OnClick;
    }

    // Raised instead of a bound ICommand: the DataContext here is whatever
    // view-model carries the icon's state (a track row, an expanded album's
    // song row, an album tile), none of which owns a command of its own - the
    // download runner lives on MainViewModel (see TrackDownloadRunner). Each
    // host decides what its own DataContext means: one track, or a whole
    // album's worth.
    public event EventHandler<DownloadIndicatorViewModel>? DownloadRequested;

    private void OnClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is DownloadIndicatorViewModel indicator)
            DownloadRequested?.Invoke(this, indicator);
        // Keeps the click from reaching the list's own pointer handling
        // underneath (MusicListPanel, AlbumGridView), which would otherwise
        // treat it as a row or tile click.
        e.Handled = true;
    }
}
