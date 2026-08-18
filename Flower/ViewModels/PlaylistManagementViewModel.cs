using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;

using Flower.Models;

using Material.Icons;

namespace Flower.ViewModels;

// Raised by DeletePlaylistAsync before it actually deletes anything - the view
// is expected to confirm with the user and report back via Confirmed, the same
// TaskCompletionSource-based handoff PlaylistConflictEventArgs uses (see
// Services/PlaylistSyncService.cs) so the ViewModel never has to know a dialog
// is involved.
public sealed class DeletePlaylistConfirmationEventArgs : EventArgs
{
    public required Playlist Playlist { get; init; }
    public required TaskCompletionSource<bool> Confirmed { get; init; }
}

// What PlaylistManagementViewModel needs from whoever owns the sidebar - the
// same shape as IDeviceSidebarHost, and for the same reason: playlist CRUD
// mutates the sidebar's Playlists section, but nothing else about it is a
// MainViewModel concern.
public interface IPlaylistManagementHost
{
    SidebarItem? SelectedSidebarItem { get; set; }

    // Where selection lands when the selected playlist is deleted or its row
    // disappears in a refresh - the Songs row.
    SidebarItem? DefaultSelection { get; }

    // A playlist currently being shown changed its track list, so the track
    // list needs rebuilding.
    void PlaylistContentChanged();

    // A local playlist edit happened - schedule the debounced peer resync.
    void ScheduleContentSync();
}

// Playlist creation, deletion, membership and ordering, plus the sidebar's
// Playlists section - one of the six unrelated jobs MainViewModel was doing
// (see docs/ARCHITECTURE-REVIEW.md Tier 4.2).
public sealed class PlaylistManagementViewModel
{
    private readonly Library _library;
    private readonly ObservableCollection<SidebarItem> _items;
    private readonly IPlaylistManagementHost _host;

    public PlaylistManagementViewModel(Library library, ObservableCollection<SidebarItem> items, IPlaylistManagementHost host)
    {
        _library = library;
        _items   = items;
        _host    = host;
    }

    // See DeletePlaylistConfirmationEventArgs above.
    public event EventHandler<DeletePlaylistConfirmationEventArgs>? DeleteConfirmationRequested;

    // Rebuilds just the "Playlists" section in place, preserving the current
    // selection by playlist Id when possible - PlaylistsUpdated replaces the
    // whole Library.Playlists list (see Library.ReplacePlaylists), so the
    // previously selected Playlist object reference may no longer be the one
    // shown.
    public void RefreshSidebarItems()
    {
        var selectedPlaylistId = _host.SelectedSidebarItem?.Kind == SidebarItemKind.Playlist
            ? _host.SelectedSidebarItem.Playlist?.Id
            : null;

        // A row mid-rename (see MainView.BeginRename/CommitRename) keeps its own
        // SidebarItem/TextBox rather than being torn down and recreated below -
        // this refresh can be triggered by a background PlaylistsUpdated (e.g. a
        // device sync landing mid-edit) with no input from the user, and rebuilding
        // the row would silently yank focus out of its TextBox, looking like the
        // rename cancelled itself.
        var editingIds = _items
            .Where(i => i.Kind == SidebarItemKind.Playlist && i.IsEditing && i.Playlist != null)
            .Select(i => i.Playlist!.Id)
            .ToHashSet();

        foreach (var stale in _items.Where(i => i.Kind == SidebarItemKind.Playlist && !editingIds.Contains(i.Playlist!.Id)).ToList())
            _items.Remove(stale);

        var header = _items.FirstOrDefault(i => i.Kind == SidebarItemKind.Header && i.Name == "Playlists");
        if (_library.Playlists.Count == 0)
        {
            if (header != null && editingIds.Count == 0)
                _items.Remove(header);
            if (selectedPlaylistId != null)
                _host.SelectedSidebarItem = _host.DefaultSelection;
            return;
        }

        var insertAt = header != null ? _items.IndexOf(header) + 1 : _items.Count;
        if (header == null)
            _items.Insert(insertAt++, new SidebarItem(SidebarItemKind.Header, "Playlists"));

        foreach (var pl in _library.Playlists)
        {
            if (editingIds.Contains(pl.Id))
                continue;
            _items.Insert(insertAt++, new SidebarItem(SidebarItemKind.Playlist, pl.Name, MaterialIconKind.PlaylistPlay, pl));
        }

        if (selectedPlaylistId != null)
        {
            var reselected = _items.FirstOrDefault(i => i.Kind == SidebarItemKind.Playlist && i.Playlist?.Id == selectedPlaylistId);
            _host.SelectedSidebarItem = reselected ?? _host.DefaultSelection;
        }
    }

    public Task CreateWithTrack(Track? track)
        => CreateWithTracks(track != null ? new List<Track> { track } : new List<Track>());

    public Task CreateWithTracks(IEnumerable<Track> tracks)
    {
        var playlist = new Playlist("New Playlist", tracks.ToList());
        _library.AddPlaylist(playlist);

        if (_items.All(i => i.Kind != SidebarItemKind.Playlist))
            _items.Add(new SidebarItem(SidebarItemKind.Header, "Playlists"));

        var sidebarItem = new SidebarItem(SidebarItemKind.Playlist, playlist.Name, MaterialIconKind.PlaylistPlay, playlist)
        {
            IsEditing = true
        };
        _items.Add(sidebarItem);

        _host.SelectedSidebarItem = sidebarItem;

        _host.ScheduleContentSync();
        return Task.CompletedTask;
    }

    public async Task DeleteAsync(Playlist playlist)
    {
        // Gated here rather than at each call site (the sidebar's context menu
        // and the Playlist main-menu command both land here) so neither one can
        // forget to confirm. No subscriber (e.g. no window yet) means proceed
        // unconfirmed, matching how PlaylistConflictRequested degrades elsewhere.
        if (DeleteConfirmationRequested is { } handler)
        {
            var confirmed = new TaskCompletionSource<bool>();
            handler.Invoke(this, new DeletePlaylistConfirmationEventArgs { Playlist = playlist, Confirmed = confirmed });
            if (!await confirmed.Task)
                return;
        }

        _library.RemovePlaylist(playlist);

        // Reuses the sidebar-rebuild logic sync already needed to reflect a
        // changed Library.Playlists (see PlaylistSyncService) - it also handles
        // falling back to Songs if the deleted playlist was selected.
        RefreshSidebarItems();

        _host.ScheduleContentSync();
    }

    // Backs the "Playlist" main-menu's Rename/Delete entries, which - unlike the
    // sidebar's own right-click menu - have no specific row to operate on, only
    // whichever playlist is currently selected.
    public bool CanRenameOrDeleteSelected() => _host.SelectedSidebarItem?.Kind == SidebarItemKind.Playlist;

    public async Task DeleteSelectedAsync()
    {
        if (_host.SelectedSidebarItem?.Playlist is { } playlist)
            await DeleteAsync(playlist);
    }

    public Task AddTrack(Track track, Playlist playlist)
        => AddTracks(new[] { track }, playlist);

    public Task AddTracks(IEnumerable<Track> tracks, Playlist playlist)
    {
        foreach (var track in tracks)
            playlist.AppendTrack(track);
        if (_host.SelectedSidebarItem?.Playlist == playlist)
            _host.PlaylistContentChanged();

        _host.ScheduleContentSync();
        return Task.CompletedTask;
    }

    public Task ReorderTrack(Playlist playlist, Track dragged, Track? insertBefore)
    {
        // Was an open-coded Remove()+Insert() on playlist.Tracks, which bumped
        // neither UpdatedAt nor Changed - so a reorder was invisible to both
        // sync and the save. See Playlist.Tracks.
        if (!playlist.MoveTrack(dragged, insertBefore))
            return Task.CompletedTask;

        if (_host.SelectedSidebarItem?.Playlist == playlist)
            _host.PlaylistContentChanged();

        _host.ScheduleContentSync();
        return Task.CompletedTask;
    }
}
