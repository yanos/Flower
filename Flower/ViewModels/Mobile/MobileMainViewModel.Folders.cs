using System.Collections.Generic;
using System.Linq;
using System.Windows.Input;

using CommunityToolkit.Mvvm.Input;

using Material.Icons;

using Flower.Models;

namespace Flower.ViewModels.Mobile;

// ── Playlist folders ──────────────────────────────────────────────────
//
// The Playlists tab shows the sidebar's own rows (Main.SidebarItems), so a
// folder is a row there already, with its playlists under it while it is
// open. What is the phone's own: a tap on a folder opens or closes it rather
// than drilling in, a new folder is named before it exists the way a new
// playlist is, and moving a playlist is a sheet of folders to pick from.
public partial class MobileMainViewModel
{
    // What the draft row is naming: a folder rather than a playlist, and the
    // folder either goes in. Both reset with the rest of the draft
    // (EndNamingNewPlaylist).
    private bool _newPlaylistIsFolder;
    private Playlist? _newPlaylistFolder;

    public string NewPlaylistPlaceholder => _newPlaylistIsFolder ? "Folder Name" : "Playlist Name";
    public MaterialIconKind NewPlaylistIcon => _newPlaylistIsFolder ? MaterialIconKind.FolderOutline : MaterialIconKind.PlaylistPlay;

    private void BeginNaming(bool folder, Playlist? parent, IReadOnlyList<Track> tracks)
    {
        _newPlaylistTracks = tracks;
        _newPlaylistIsFolder = folder;
        _newPlaylistFolder = parent;
        OnPropertyChanged(nameof(NewPlaylistPlaceholder));
        OnPropertyChanged(nameof(NewPlaylistIcon));
        NewPlaylistName = "";
        IsNamingNewPlaylist = true;
    }

    private void ForgetNamingTarget()
    {
        if (!_newPlaylistIsFolder && _newPlaylistFolder == null)
            return;
        _newPlaylistIsFolder = false;
        _newPlaylistFolder = null;
        OnPropertyChanged(nameof(NewPlaylistPlaceholder));
        OnPropertyChanged(nameof(NewPlaylistIcon));
    }

    // Whatever the draft row was naming, once it has a name.
    private System.Threading.Tasks.Task CreateNamed(string name, IReadOnlyList<Track> tracks, bool folder, Playlist? parent) =>
        folder
            ? Main.Playlists.CreateFolderNamed(name, parent)
            : Main.Playlists.CreateNamed(name, tracks, parent);

    // The Playlists tab's New Folder row: a folder at the top level.
    public ICommand BeginCreateFolderCommand => _beginCreateFolder ??=
        new RelayCommand(() => BeginNaming(folder: true, parent: null, []));

    // From a folder's own menu - the draft row is at the top of the tab, and
    // what it names goes inside the folder whose menu it was.
    private void BeginNamingInActionTargetFolder(bool folder)
    {
        var parent = PlaylistActionTarget?.Playlist;
        ActiveSheet = MobileSheet.None;
        if (parent is { IsFolder: true })
            BeginNaming(folder, parent, []);
    }

    private ICommand NewPlaylistInFolderCommand => _newPlaylistInFolder ??=
        new RelayCommand(() => BeginNamingInActionTargetFolder(folder: false));

    private ICommand NewFolderInFolderCommand => _newFolderInFolder ??=
        new RelayCommand(() => BeginNamingInActionTargetFolder(folder: true));

    // A tap on a folder's row, on the tab or in the add-to-playlist sheet.
    private bool ToggleIfFolder(SidebarItem? item)
    {
        if (item?.Playlist is not { IsFolder: true } folder)
            return false;
        Main.Playlists.ToggleFolder(folder);
        return true;
    }

    // ── Move to Folder ────────────────────────────────────────────────

    // One line of the Move to Folder sheet: a folder, or the top level for
    // null, indented as the tab shows it.
    public sealed record FolderChoice(Playlist? Folder, string Name, int Depth, bool IsCurrent)
    {
        public Avalonia.Thickness Indent => new(20 + Depth * 20, 14, 20, 14);
        public MaterialIconKind Icon => Folder == null ? MaterialIconKind.FormatListBulleted : MaterialIconKind.FolderOutline;
    }

    private Playlist? _moving;

    public IReadOnlyList<FolderChoice> FolderChoices { get; private set; } = [];

    public string MovingPlaylistName => _moving?.Name ?? "";

    public bool CanMoveActionTargetToFolder =>
        PlaylistActionTarget?.Playlist is { } playlist && FolderChoicesFor(playlist).Count > 1;

    private List<FolderChoice> FolderChoicesFor(Playlist playlist)
    {
        var all = Main.Library.Playlists;
        var current = PlaylistTree.ParentOf(playlist, all);
        var choices = new List<FolderChoice> { new(null, "Top Level", 0, current == null) };
        choices.AddRange(PlaylistTree.FoldersFor([playlist], all)
            .Select(row => new FolderChoice(row.Playlist, row.Playlist.Name, row.Depth, row.Playlist == current)));
        return choices;
    }

    private ICommand OpenMoveToFolderCommand => _openMoveToFolder ??= new RelayCommand(() =>
    {
        if (PlaylistActionTarget?.Playlist is not { } playlist)
            return;
        _moving = playlist;
        FolderChoices = FolderChoicesFor(playlist);
        OnPropertyChanged(nameof(FolderChoices));
        OnPropertyChanged(nameof(MovingPlaylistName));
        ActiveSheet = MobileSheet.MoveToFolder;
    });

    public ICommand MoveToFolderCommand => _moveToFolder ??= new RelayCommand<FolderChoice>(async choice =>
    {
        var playlist = _moving;
        ActiveSheet = MobileSheet.None;
        if (playlist != null && choice is { IsCurrent: false })
            await Main.MovePlaylists([playlist], choice.Folder);
    });

    public bool IsShowingMoveToFolder => ActiveSheet == MobileSheet.MoveToFolder;

    private ICommand? _beginCreateFolder;
    private ICommand? _newPlaylistInFolder;
    private ICommand? _newFolderInFolder;
    private ICommand? _openMoveToFolder;
    private ICommand? _moveToFolder;
}
