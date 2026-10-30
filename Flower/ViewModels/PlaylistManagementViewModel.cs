using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;

using Flower.Models;

namespace Flower.ViewModels;

// Raised by DeletePlaylistAsync before it actually deletes anything - the view
// is expected to confirm with the user and report back via Confirmed, the same
// TaskCompletionSource-based handoff PlaylistConflictEventArgs uses (see
// Services/PlaylistSyncService.cs) so the ViewModel never has to know a dialog
// is involved.
public sealed class DeletePlaylistConfirmationEventArgs : EventArgs
{
    // At least one; several when they were selected together in the sidebar,
    // confirmed by one dialog rather than one each.
    public required IReadOnlyList<Playlist> Playlists { get; init; }
    public required TaskCompletionSource<bool> Confirmed { get; init; }
}

// What PlaylistManagementViewModel needs from whoever owns the sidebar - the
// same shape as IDeviceSidebarHost, and for the same reason: playlist CRUD
// mutates the sidebar's Playlists section, but nothing else about it is a
// MainViewModel concern.
public interface IPlaylistManagementHost
{
    SidebarItem? SelectedSidebarItem { get; set; }

    // Every playlist selected in the sidebar - several on desktop after a
    // Cmd/Ctrl+click, where SelectedSidebarItem is only the one being shown.
    IReadOnlyList<Playlist> SelectedPlaylists { get; }

    // Where selection lands when the selected playlist is deleted or its row
    // disappears in a refresh - the Songs row.
    SidebarItem? DefaultSelection { get; }

    // A playlist currently being shown changed its track list, so the track
    // list needs rebuilding.
    void PlaylistContentChanged();

    // A local playlist edit happened - schedule the debounced peer resync.
    void ScheduleContentSync();

    // A folder was opened or closed in the sidebar, so the set handed to the
    // constructor as collapsedFolders changed and wants saving. Which folders
    // are closed is this device's own view of the sidebar and never syncs.
    void PlaylistFoldersCollapsedChanged() { }
}

// Playlist creation, deletion, membership and ordering, plus the sidebar's
// Playlists section - one of the six unrelated jobs MainViewModel was doing
// (see docs/ARCHITECTURE-REVIEW.md Tier 4.2).
public sealed class PlaylistManagementViewModel
{
    private readonly Library _library;
    private readonly ObservableCollection<SidebarItem> _items;
    private readonly IPlaylistManagementHost _host;

    // The folders shown closed in the sidebar, by Playlist.Id's key - the
    // caller's own collection (AppSettings.CollapsedPlaylistFolders), so a
    // change is already in what the host saves.
    private readonly ICollection<string> _collapsedFolders;

    public PlaylistManagementViewModel(
        Library library, ObservableCollection<SidebarItem> items, IPlaylistManagementHost host,
        ICollection<string>? collapsedFolders = null)
    {
        _library = library;
        _items   = items;
        _host    = host;
        _collapsedFolders = collapsedFolders ?? new List<string>();
    }

    // See DeletePlaylistConfirmationEventArgs above.
    public event EventHandler<DeletePlaylistConfirmationEventArgs>? DeleteConfirmationRequested;

    // Rebuilds just the "Playlists" section in place, preserving the current
    // selection by playlist Id when possible - PlaylistsUpdated replaces the
    // whole Library.Playlists list (see Library.ReplacePlaylists), so the
    // previously selected Playlist object reference may no longer be the one
    // shown.
    public void RefreshSidebarItems() => RefreshSidebarItems(renaming: null);

    // renaming is a playlist just created, whose row comes back already in
    // its inline rename box - built that way rather than flipped afterwards,
    // so its TextBox is realized fresh and RenameBox_Loaded focuses it.
    private void RefreshSidebarItems(Playlist? renaming)
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

        var insertAt = header != null ? _items.IndexOf(header) + 1 : SectionAfterLibrary();
        if (header == null)
            _items.Insert(insertAt++, new SidebarItem(SidebarItemKind.Header, "Playlists"));

        foreach (var row in CreateSidebarRows())
        {
            if (editingIds.Contains(row.Playlist!.Id))
                continue;
            if (row.Playlist == renaming)
                StartNaming(row);
            _items.Insert(insertAt++, row);
        }

        if (selectedPlaylistId != null)
            _host.SelectedSidebarItem = RowShowing(selectedPlaylistId.Value) ?? _host.DefaultSelection;
    }

    // The Playlists section's rows, in tree order, without its heading - for
    // the refresh above and for MainViewModel's first build of the sidebar.
    public List<SidebarItem> CreateSidebarRows()
    {
        var all = _library.Playlists;
        var sizes = all
            .Select(p => PlaylistTree.ParentOf(p, all))
            .OfType<Playlist>()
            .GroupBy(folder => folder.Id)
            .ToDictionary(g => g.Key, g => g.Count());

        return PlaylistTree.Flatten(all, IsCollapsed)
            .Select(row => new SidebarItem(SidebarItemKind.Playlist, row.Playlist.Name, SidebarItem.IconFor(row.Playlist), row.Playlist)
            {
                Depth = row.Depth,
                IsExpanded = !IsCollapsed(row.Playlist),
                FolderSize = sizes.GetValueOrDefault(row.Playlist.Id),
            })
            .ToList();
    }

    // The row for a playlist, or while it is hidden in a closed folder the
    // row of the folder it is hidden in - which is where the selection goes
    // when the folder it was in is closed over it.
    private SidebarItem? RowShowing(Guid playlistId)
    {
        var playlist = _library.Playlists.FirstOrDefault(p => p.Id == playlistId);
        for (var at = playlist; at != null; at = PlaylistTree.ParentOf(at, _library.Playlists))
        {
            if (_items.FirstOrDefault(i => i.Kind == SidebarItemKind.Playlist && i.Playlist == at) is { } row)
                return row;
        }
        return null;
    }

    private bool IsCollapsed(Playlist playlist) => playlist.IsFolder && _collapsedFolders.Contains(playlist.Id.ToKey());

    // ── Folders ───────────────────────────────────────────────────────────────

    public void SetFolderExpanded(Playlist folder, bool expanded)
    {
        if (!folder.IsFolder || IsCollapsed(folder) != expanded)
            return;

        if (expanded)
            _collapsedFolders.Remove(folder.Id.ToKey());
        else
            _collapsedFolders.Add(folder.Id.ToKey());

        _host.PlaylistFoldersCollapsedChanged();
        RefreshSidebarItems();
    }

    public void ToggleFolder(Playlist folder) => SetFolderExpanded(folder, IsCollapsed(folder));

    // Opens a folder and every folder it sits in, so a playlist just put
    // there can be seen arriving.
    private void Reveal(Playlist? folder)
    {
        var changed = false;
        for (var at = folder; at != null; at = PlaylistTree.ParentOf(at, _library.Playlists))
            changed |= _collapsedFolders.Remove(at.Id.ToKey());

        if (changed)
            _host.PlaylistFoldersCollapsedChanged();
    }

    // A new folder, inside the one given or at the top level, opened in its
    // rename box the way a new playlist is.
    public Task CreateFolder(Playlist? parent)
    {
        var folder = Playlist.NewFolder("New Folder", parent is { IsFolder: true } ? parent.Id : null);
        AddAndRename(folder);
        return Task.CompletedTask;
    }

    // Puts each playlist in the folder, or at the top level for null - every
    // one that can go there, and the rest (the folder itself, or one it sits
    // in) left where they are.
    public Task Move(IReadOnlyList<Playlist> playlists, Playlist? folder)
    {
        var moved = false;
        foreach (var playlist in playlists)
        {
            if (!PlaylistTree.CanMove(playlist, folder, _library.Playlists))
                continue;
            if (playlist.ParentId == folder?.Id)
                continue;

            playlist.ParentId = folder?.Id;
            moved = true;
        }

        if (!moved)
            return Task.CompletedTask;

        Reveal(folder);
        RefreshSidebarItems();
        _host.ScheduleContentSync();
        return Task.CompletedTask;
    }

    // Whether the list on screen is made of this playlist's songs: it is
    // the one shown, or it is in the folder shown.
    private bool IsShowing(Playlist playlist) =>
        _host.SelectedSidebarItem?.Playlist is { } shown
        && (shown == playlist || shown.IsFolder && PlaylistTree.DescendantsOf(shown, _library.Playlists).Contains(playlist));

    // Where a new playlist's row goes: under the last playlist, or under a
    // Playlists header made for it where there was none. Never the end of the
    // list, which is under the Servers section when a server is paired.
    private int EndOfPlaylists()
    {
        var header = _items.FirstOrDefault(i => i.Kind == SidebarItemKind.Header && i.Name == "Playlists");
        if (header == null)
        {
            var at = SectionAfterLibrary();
            _items.Insert(at, new SidebarItem(SidebarItemKind.Header, "Playlists"));
            return at + 1;
        }

        var end = _items.IndexOf(header) + 1;
        while (end < _items.Count && _items[end].Kind == SidebarItemKind.Playlist)
            end++;
        return end;
    }

    // The Playlists section's place when it has none yet: straight after the
    // Library section, before whatever heading follows it (Servers, Server).
    private int SectionAfterLibrary()
    {
        for (var i = 1; i < _items.Count; i++)
        {
            if (_items[i].Kind == SidebarItemKind.Header)
                return i;
        }
        return _items.Count;
    }

    public Task CreateWithTrack(Track? track)
        => CreateWithTracks(track != null ? new List<Track> { track } : new List<Track>());

    // In the folder given, when there is one - New Playlist on a folder's
    // menu, or songs dropped on a folder's row.
    public Task CreateWithTracks(IEnumerable<Track> tracks, Playlist? folder = null)
    {
        var playlist = new Playlist("New Playlist", tracks.ToList())
        {
            ParentId = folder is { IsFolder: true } ? folder.Id : null,
        };
        AddAndRename(playlist);
        return Task.CompletedTask;
    }

    private void AddAndRename(Playlist playlist)
    {
        _library.AddPlaylist(playlist);

        // At the top level a new row goes at the end of the section, where
        // the tree puts it anyway; in a folder it goes where the tree says,
        // with the folder opened to show it.
        if (playlist.ParentId == null)
        {
            var sidebarItem = new SidebarItem(SidebarItemKind.Playlist, playlist.Name, SidebarItem.IconFor(playlist), playlist)
            {
                IsExpanded = true,
            };
            StartNaming(sidebarItem);
            _items.Insert(EndOfPlaylists(), sidebarItem);
        }
        else
        {
            Reveal(PlaylistTree.ParentOf(playlist, _library.Playlists));
            RefreshSidebarItems(renaming: playlist);
        }

        // No content sync yet: until it has a name it may still be taken back
        // out (DiscardNew), and the server is told once it is named - by the
        // rename's own commit, SidebarRenameService.CommitAsync.
        _host.SelectedSidebarItem = _items.FirstOrDefault(i => i.Playlist == playlist);
    }

    // The new row's box opens empty rather than holding the placeholder name
    // to type over, so there is no name to accept by pressing Enter. The
    // playlist itself keeps "New Playlist" until then, which is only ever
    // seen if the app goes away mid-naming.
    private static void StartNaming(SidebarItem row)
    {
        row.Name = "";
        row.IsNewPlaylist = true;
        row.IsEditing = true;
    }

    // A playlist created and then committed with no name: out of the library
    // and the sidebar again, as if it had never been made.
    public void DiscardNew(SidebarItem row)
    {
        if (row.Playlist is not { } playlist)
            return;

        var wasSelected = _host.SelectedSidebarItem == row;
        _items.Remove(row);
        _library.RemovePlaylist(playlist);
        if (wasSelected)
            _host.SelectedSidebarItem = _host.DefaultSelection;
    }

    // Named before it exists, rather than created as "New Playlist" and renamed
    // in place: a phone has no sidebar row to type into after the fact, so the
    // mobile flow shows an empty, focused box first and only gets here once the
    // user has actually typed something (see MobileMainViewModel's
    // BeginCreatePlaylistCommand/CommitNewPlaylistCommand). Which also means
    // no row is ever left behind by a create the user changed their mind about,
    // and nothing here comes back IsEditing.
    public Task CreateNamed(string name, IEnumerable<Track> tracks, Playlist? folder = null)
    {
        var playlist = new Playlist(name, tracks.ToList())
        {
            ParentId = folder is { IsFolder: true } ? folder.Id : null,
        };
        return AddNamed(playlist);
    }

    // The phone's New Folder, named before it exists for the same reason.
    public Task CreateFolderNamed(string name, Playlist? parent = null) =>
        AddNamed(Playlist.NewFolder(name, parent is { IsFolder: true } ? parent.Id : null));

    private Task AddNamed(Playlist playlist)
    {
        _library.AddPlaylist(playlist);
        Reveal(PlaylistTree.ParentOf(playlist, _library.Playlists));
        RefreshSidebarItems();

        _host.SelectedSidebarItem = _items.FirstOrDefault(i => i.Playlist == playlist);

        _host.ScheduleContentSync();
        return Task.CompletedTask;
    }

    public Task DeleteAsync(Playlist playlist) => DeleteAsync(new[] { playlist });

    public async Task DeleteAsync(IReadOnlyList<Playlist> playlists)
    {
        if (playlists.Count == 0)
            return;

        // Gated here rather than at each call site (the sidebar's context menu
        // and the Playlist main-menu command both land here) so neither one can
        // forget to confirm. No subscriber (e.g. no window yet) means proceed
        // unconfirmed, matching how PlaylistConflictRequested degrades elsewhere.
        if (DeleteConfirmationRequested is { } handler)
        {
            var confirmed = new TaskCompletionSource<bool>();
            handler.Invoke(this, new DeletePlaylistConfirmationEventArgs { Playlists = playlists, Confirmed = confirmed });
            if (!await confirmed.Task)
                return;
        }

        // A folder's playlists are not deleted with it: they move up to
        // wherever the folder was, so deleting a folder only ever deletes
        // what was selected. Done before the removal, while the tree still
        // has the folders in it to walk.
        var doomed = playlists.Select(p => p.Id).ToHashSet();
        foreach (var playlist in _library.Playlists)
        {
            if (doomed.Contains(playlist.Id) || PlaylistTree.ParentOf(playlist, _library.Playlists) is not { } parent || !doomed.Contains(parent.Id))
                continue;

            var to = parent;
            while (to != null && doomed.Contains(to.Id))
                to = PlaylistTree.ParentOf(to, _library.Playlists);
            playlist.ParentId = to?.Id;
        }

        foreach (var playlist in playlists)
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

    // Every selected playlist, not just the one on screen - the selection is
    // what the user sees highlighted, and a menu command deleting a part of it
    // would be deleting something other than what it appears to.
    public async Task DeleteSelectedAsync()
    {
        if (_host.SelectedPlaylists.Count > 0)
            await DeleteAsync(_host.SelectedPlaylists);
        else if (_host.SelectedSidebarItem?.Playlist is { } playlist)
            await DeleteAsync(playlist);
    }

    public Task AddTrack(Track track, Playlist playlist)
        => AddTracks(new[] { track }, playlist);

    public Task AddTracks(IEnumerable<Track> tracks, Playlist playlist)
    {
        // A smart playlist's members are the result of its rules, so an added
        // track would be silently dropped by the next recompute. Refused here
        // rather than at each call site (the sidebar drop, the Add To Playlist
        // menu, the mobile view) so none of them can forget - the UI also hides
        // the affordance, and this is what makes that a presentation detail
        // rather than the only thing standing between a user and a confusing
        // no-op. See docs/SMART-PLAYLIST-PLAN.md, "UI". A folder has no songs
        // of its own to add to, for the same effect.
        if (!playlist.IsOrdinary)
            return Task.CompletedTask;

        foreach (var track in tracks)
            playlist.AppendTrack(track);
        if (IsShowing(playlist))
            _host.PlaylistContentChanged();

        _host.ScheduleContentSync();
        return Task.CompletedTask;
    }

    public Task ReorderTrack(Playlist playlist, Track dragged, Track? insertBefore)
    {
        // Same reasoning as AddTracks: the order of a smart playlist is the
        // order its rules produced, and a drag would last until the next pass.
        // A folder's order is its playlists'.
        if (!playlist.IsOrdinary)
            return Task.CompletedTask;

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

    // A smart playlist starts life ordinary, empty and unnamed-by-the-user, and
    // becomes smart the moment the editor saves rules onto it. That ordering is
    // what lets the editor be an ordinary editor over an existing playlist
    // rather than a two-mode thing that sometimes creates and sometimes edits -
    // and SmartPlaylistEditorViewModel.Cancel is what removes it again if the
    // user changes their mind.
    public Playlist CreateSmart()
    {
        var playlist = new Playlist("New Smart Playlist", new List<Track>());
        _library.AddPlaylist(playlist);
        RefreshSidebarItems();

        _host.SelectedSidebarItem = _items.FirstOrDefault(i => i.Playlist == playlist);
        return playlist;
    }

    // Freeze what the rules currently produced and forget them - the playlist
    // keeps exactly the songs it is showing and becomes hand-editable. A real
    // user edit, so unlike a recompute it bumps UpdatedAt and syncs. The
    // reverse is not offered: there is nothing to turn a hand-picked list into.
    public Task ConvertToOrdinary(Playlist playlist)
    {
        if (!playlist.IsSmart)
            return Task.CompletedTask;

        playlist.Rules = null;
        RefreshSidebarItems();

        _host.ScheduleContentSync();
        return Task.CompletedTask;
    }
}
