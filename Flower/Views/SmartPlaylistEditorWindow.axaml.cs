using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;

using Avalonia.Controls;
using Avalonia.Interactivity;

using Flower.Models;
using Flower.Services;
using Flower.ViewModels;

namespace Flower.Views;

// The rule editor's window. Everything it decides lives in
// SmartPlaylistEditorViewModel and the controls in SmartPlaylistEditorView;
// this is the two clicks that end an edit, and the preview's track list.
//
// Modal (ShowDialog), unlike the Equalizer and Log windows: those are things
// you leave open while the music plays, and this one has an OK button whose
// whole meaning is that the playlist is not yet what it says it is.
public partial class SmartPlaylistEditorWindow : Window
{
    private readonly MainViewModel? _main;

    // The preview in the rules' own order until a column header is clicked;
    // a sort here only changes how the preview reads, not the playlist.
    private string _sortColumn = "PlaylistOrder";
    private bool _sortAscending = true;

    // The songs as the list shows them, for playing from one.
    private List<Track> _shown = [];

    // Satisfies Avalonia's runtime-XAML-loader/previewer check (AVLN3001) -
    // never called directly; the real constructor below is what's used.
    public SmartPlaylistEditorWindow() => InitializeComponent();

    public SmartPlaylistEditorWindow(SmartPlaylistEditorViewModel viewModel, MainViewModel main)
    {
        InitializeComponent();
        DataContext = viewModel;
        _main = main;

        viewModel.PropertyChanged += OnEditorChanged;
        Closed += (_, _) => viewModel.PropertyChanged -= OnEditorChanged;

        PreviewList.SortRequested += OnSortRequested;
        PreviewList.RowActivated += OnRowActivated;
        PreviewList.RowContextMenu += OnRowContextMenu;
        ShowPreview();
    }

    private SmartPlaylistEditorViewModel? ViewModel => DataContext as SmartPlaylistEditorViewModel;

    private void OnEditorChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(SmartPlaylistEditorViewModel.PreviewTracks))
            ShowPreview();
    }

    private void ShowPreview()
    {
        if (_main == null || ViewModel is not { } editor)
            return;

        var rows = _main.SmartPlaylistPreviewRows(editor.PreviewTracks, _sortColumn, _sortAscending);
        _shown = rows.Select(r => r.Track).ToList();
        PreviewList.SetItems(rows);
        PreviewList.UpdateSortIndicators(_sortColumn == "PlaylistOrder" ? "" : _sortColumn, _sortAscending);
    }

    // The same column clicked again turns it round, as on the main list.
    private void OnSortRequested(object? sender, string column)
    {
        _sortAscending = column != _sortColumn || !_sortAscending;
        _sortColumn = column;
        ShowPreview();
    }

    // Double-click or Enter plays from there, with the preview as the queue -
    // what tapping a preview row does on the phone.
    private void OnRowActivated(object? sender, TrackRowViewModel row)
    {
        _main?.PlayTrackInAlbum(_shown, row.Track);
    }

    // The song menu, over the preview's selection in the order it shows
    // (DesktopSongMenu). No Go to Album or Artist: the main window is behind
    // this one, and navigating it from here would happen out of sight.
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
}
