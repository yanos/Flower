using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;

using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Threading;

using Flower.Models;
using Flower.Services;
using Flower.ViewModels;

namespace Flower.Views;

// The Vibe Playlist editor's view. What it decides lives in
// VibePlaylistEditorViewModel; this is the preview's track list and the two
// clicks that end an edit, which raise Closed for whichever host it is in.
public partial class VibePlaylistEditorView : UserControl
{
    private readonly MainViewModel? _main;

    // The songs as the list shows them, best first.
    private List<Track> _shown = [];

    // Satisfies Avalonia's runtime-XAML-loader/previewer check (AVLN3001).
    public VibePlaylistEditorView() => InitializeComponent();

    public VibePlaylistEditorView(VibePlaylistEditorViewModel viewModel, MainViewModel main)
    {
        InitializeComponent();
        DataContext = viewModel;
        _main = main;

        viewModel.PropertyChanged += OnEditorChanged;
        DetachedFromVisualTree += (_, _) => viewModel.PropertyChanged -= OnEditorChanged;
        AttachedToVisualTree += (_, _) => Dispatcher.UIThread.Post(() => PromptBox.Focus());

        PreviewList.RowActivated += OnRowActivated;
        PreviewList.RowContextMenu += OnRowContextMenu;
        ShowPreview();
    }

    // True when the edit was saved.
    public event EventHandler<bool>? Closed;

    private VibePlaylistEditorViewModel? ViewModel => DataContext as VibePlaylistEditorViewModel;

    private void OnEditorChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(VibePlaylistEditorViewModel.PreviewTracks))
            ShowPreview();
    }

    // In the ranking's order; a column click doesn't reorder a ranking.
    private void ShowPreview()
    {
        if (_main == null || ViewModel is not { } editor)
            return;

        var rows = _main.SmartPlaylistPreviewRows(editor.PreviewTracks, "PlaylistOrder", true);
        _shown = rows.Select(r => r.Track).ToList();
        PreviewList.SetItems(rows);
    }

    // Double-click or Enter plays from there, with the preview as the queue,
    // as the smart playlist editor's does.
    private void OnRowActivated(object? sender, TrackRowViewModel row) =>
        _main?.PlayTrackInAlbum(_shown, row.Track);

    private void OnRowContextMenu(object? sender, TrackRowViewModel row)
    {
        if (_main == null)
            return;

        var selected = PreviewList.SelectedRows.Select(r => r.Track).ToHashSet();
        var inOrder = _shown.Where(selected.Contains).ToList();
        PlatformContextMenu.Open(
            DesktopSongMenu.Build(this, _main, row.Track, inOrder, _shown, canGoToAlbum: false, canGoToArtist: false),
            PreviewList);
    }

    private void Ok_Click(object? sender, RoutedEventArgs e)
    {
        if (ViewModel?.Save() == true)
            Closed?.Invoke(this, true);
    }

    private void Cancel_Click(object? sender, RoutedEventArgs e)
    {
        ViewModel?.Cancel();
        Closed?.Invoke(this, false);
    }
}
