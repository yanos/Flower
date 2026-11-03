using System.Collections.Generic;
using System.ComponentModel;
using System.Windows.Input;

using CommunityToolkit.Mvvm.Input;

using Flower.Models;

namespace Flower.ViewModels.Mobile;

// ── Vibe Playlists ────────────────────────────────────────────────────────
//
// The phone's half of docs/VIBE-PLAYLIST-PLAN.md, phase 4: the editor the
// desktop shows as a dialog, here as a pushed screen like the smart playlist
// editor's, and a Vibe Playlist playing as its endless queue from every door
// an ordinary playlist plays from. Nothing is worked out on the phone; it asks
// its server, through the same MainViewModel calls the desktop makes.
public partial class MobileMainViewModel
{
    public bool IsShowingVibePlaylistEditor => ActiveSheet == MobileSheet.VibePlaylistEditor;

    // Offered while the paired server makes them - the same condition the
    // desktop's menus check as they open, followed here as it changes since
    // this row is on screen all the time.
    public bool CanCreateVibePlaylist => Main.CanMakeVibePlaylists;

    public ICommand NewVibePlaylistCommand => _newVibePlaylist ??= new RelayCommand(Main.NewVibePlaylist);

    // Stays open while there is no sentence, as the desktop's OK is disabled.
    public ICommand SaveVibePlaylistCommand => _saveVibePlaylist ??= new RelayCommand(() =>
    {
        if (VibePlaylistEditor?.Save() != true)
            return;
        FinishVibePlaylistEdit(saved: true);
        ActiveSheet = MobileSheet.None;
    });

    public ICommand EditVibeActionTargetCommand => _editVibeActionTarget ??= new RelayCommand(() =>
    {
        var playlist = PlaylistActionTarget?.Playlist;
        ActiveSheet = MobileSheet.None;
        if (playlist != null)
            Main.EditVibePlaylist(playlist);
    });

    private ICommand? _newVibePlaylist;
    private ICommand? _saveVibePlaylist;
    private ICommand? _editVibeActionTarget;

    // The editor the sheet is showing, the same view model the desktop's
    // dialog binds. Not cleared when the sheet closes: it is what the sheet is
    // still showing while it slides away.
    private VibePlaylistEditorViewModel? _vibePlaylistEditor;
    public VibePlaylistEditorViewModel? VibePlaylistEditor
    {
        get => _vibePlaylistEditor;
        private set
        {
            if (_vibePlaylistEditor != null)
                _vibePlaylistEditor.PropertyChanged -= OnVibePlaylistEditorChanged;
            _vibePlaylistEditor = value;
            if (_vibePlaylistEditor != null)
                _vibePlaylistEditor.PropertyChanged += OnVibePlaylistEditorChanged;
            OnPropertyChanged();
            RebuildVibePlaylistPreviewRows();
        }
    }

    // The songs the sentence would make, in the Songs tab's own rows and in
    // the ranking's order, best first.
    private List<TrackRowViewModel> _vibePlaylistPreviewRows = [];
    public List<TrackRowViewModel> VibePlaylistPreviewRows
    {
        get => _vibePlaylistPreviewRows;
        private set
        {
            _vibePlaylistPreviewRows = value;
            OnPropertyChanged();
        }
    }

    private void OnVibePlaylistEditorChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(VibePlaylistEditorViewModel.PreviewTracks))
            RebuildVibePlaylistPreviewRows();
    }

    private void RebuildVibePlaylistPreviewRows() =>
        VibePlaylistPreviewRows = Main.SmartPlaylistPreviewRows(_vibePlaylistEditor?.PreviewTracks ?? [], "PlaylistOrder", true);

    // Whether the editor on screen has yet to be saved or cancelled.
    private bool _vibePlaylistEditPending;

    private void OpenVibePlaylistEditor(VibePlaylistEditorEventArgs e)
    {
        if (Main.VibePlaylists is not { } vibes)
            return;

        // Anything already open goes first, so a pending editor is cancelled
        // before its replacement takes the field.
        ActiveSheet = MobileSheet.None;
        VibePlaylistEditor = new VibePlaylistEditorViewModel(e.Playlist, Main.Library, vibes, e.IsNew);
        _vibePlaylistEditPending = true;
        ActiveSheet = MobileSheet.VibePlaylistEditor;
    }

    private void FinishVibePlaylistEdit(bool saved)
    {
        if (!_vibePlaylistEditPending)
            return;
        _vibePlaylistEditPending = false;

        if (!saved)
            VibePlaylistEditor?.Cancel();

        // The list follows a rename, and a save goes to the server now - an
        // edit in place starts no sync of its own.
        if (VibePlaylistEditor is { } editor)
            Main.Playlists.EditEnded(editor.Playlist, editor.IsNew, saved);
    }

    // A playlist that plays as its endless queue here: a Vibe Playlist, on a
    // device with a server to ask for more.
    private bool PlaysAsVibe(Playlist? playlist) => playlist is { IsVibe: true } && Main.VibePlaylists != null;

    // The Vibe Playlist whose own songs a tap is on: its screen, not Search,
    // the Queue, Home or an editor's preview laid over it.
    private Playlist? VibeOnScreen =>
        PlaysAsVibe(CurrentPlaylist) && !IsShowingSearchResults && !IsShowingQueue && !IsShowingHome
        && !IsShowingSmartPlaylistEditor && !IsShowingVibePlaylistEditor
            ? CurrentPlaylist
            : null;

    // The header's Play and Shuffle, the playlist row's Play, and a tapped
    // song: each starts the endless queue (from that song, for a tap) rather
    // than the pool as it stands. Shuffle too, as on the desktop - the queue
    // is drawn from the pool, so it is a shuffle already.
    private bool PlayVibeOnScreen(Track? first = null)
    {
        if (VibeOnScreen is not { } playlist)
            return false;
        Main.PlayVibePlaylist(playlist, first);
        return true;
    }

    private void OnMainChangedForVibes(string? propertyName)
    {
        if (propertyName == nameof(MainViewModel.CanMakeVibePlaylists))
            OnPropertyChanged(nameof(CanCreateVibePlaylist));
    }
}
