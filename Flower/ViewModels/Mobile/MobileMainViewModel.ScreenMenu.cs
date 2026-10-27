using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;

using Material.Icons;

using Flower.Models;
using Flower.Services;

namespace Flower.ViewModels.Mobile;

// One row of a screen's header menu (ScreenSlot's hamburger). A plain action
// rather than a command: the menu is built for the screen it is opened on, at
// the moment it is opened, so nothing about it has to stay live.
//
// Direction is set on the one sort entry in use, and says which way it runs;
// null on every other entry. IsDestructive draws its words in the danger
// colour (AppDangerBrush).
public sealed record ScreenMenuEntry(string Label, MaterialIconKind Icon, Action Invoke,
    ListSortDirection? Direction = null, bool IsDestructive = false);

// Entries under an optional small heading, drawn with a divider above every
// section but the first.
public sealed record ScreenMenuSection(string? Header, IReadOnlyList<ScreenMenuEntry> Entries);

public partial class MobileMainViewModel
{
    // ── Screen menu ───────────────────────────────────────────────────────
    //
    // The hamburger in every screen's header band, in place of the gear it
    // replaced. Settings is always there, last. Above it, what can be done to
    // the whole of what the screen shows - shuffle it, which every screen
    // offers, download it, put it in a playlist -
    // and the orders it can be shown in, but only where the answer would mean
    // something: a sort on an album's own track list, or a Download All over
    // the whole library, would be an entry that is either a no-op or a trap.

    // What can be sorted, and how, is BrowseSort's - shared with the
    // desktop's Sort button - and where the choice is kept MainViewModel's.

    private static BrowseSortScreen? SortScreenFor(MobileNavigationFrame frame) => frame.ScreenKind switch
    {
        MobileScreenKind.TrackList when frame.IsSongsList => BrowseSortScreen.Songs,
        MobileScreenKind.AlbumGrid => BrowseSortScreen.Albums,
        MobileScreenKind.ArtistAlbumGrid => BrowseSortScreen.ArtistAlbums,
        MobileScreenKind.ArtistPicker => BrowseSortScreen.Artists,
        _ => null,
    };

    private (BrowseSortOrder Order, bool Ascending) SortOf(BrowseSortScreen screen) => Main.SortOf(screen);

    // The flat Songs list's order as TrackListBuilder names it.
    private (string Column, bool Ascending) SongsColumn()
    {
        var (order, ascending) = SortOf(BrowseSortScreen.Songs);
        var column = order switch
        {
            BrowseSortOrder.Artist => "Artist",
            BrowseSortOrder.Album => "Album",
            BrowseSortOrder.Year => "Year",
            BrowseSortOrder.DateAdded => "DateAdded",
            _ => "Title",
        };
        return (column, ascending);
    }

    // An album grid in the order its screen was given.
    private List<AlbumTileViewModel> InGridOrder(List<AlbumTileViewModel> tiles, BrowseSortScreen screen) =>
        BrowseSort.InGridOrder(tiles, SortOf(screen));

    // The artist list in the order its screen was given.
    private List<ArtistPickerRow> InArtistOrder(List<ArtistPickerRow> rows, Dictionary<string, List<Track>> byArtist) =>
        BrowseSort.InArtistOrder(rows, r => byArtist.GetValueOrDefault(r.Name) ?? [], SortOf(BrowseSortScreen.Artists));

    // Whether a screen's list is sorted on something with a letter, and so
    // whether its index bar can be letters (ScrollIndexBar.ShowsLetters). Any
    // other order - a year, a date, a count - has no letter to jump to, and
    // the bar is dots, a scrubber over the whole scroll. The ZToA pair turns
    // the bar round with a reversed sort (ScrollIndexBar.LettersDescending).
    // The grid's and the artists' are the desktop's too (MainViewModel).
    public bool AlbumGridIsAlphabetical => Main.AlbumGridIsAlphabetical;

    public bool AlbumGridRunsZToA => Main.AlbumGridRunsZToA;

    public bool ArtistPickerIsAlphabetical => Main.ArtistsAreAlphabetical;

    public bool ArtistPickerRunsZToA => Main.ArtistsRunZToA;

    public bool SongsAreAlphabetical =>
        SortOf(BrowseSortScreen.Songs).Order is BrowseSortOrder.Name or BrowseSortOrder.Artist or BrowseSortOrder.Album;

    public bool SongsRunZToA => SongsAreAlphabetical && !SortOf(BrowseSortScreen.Songs).Ascending;

    // What a song is filed under on the bar: the sort-as value of whatever the
    // list is sorted on, as TrackListBuilder sorts it.
    public string? SongLetterText(Track track) => SortOf(BrowseSortScreen.Songs).Order switch
    {
        BrowseSortOrder.Artist => track.ArtistsSortValue,
        BrowseSortOrder.Album => track.AlbumSortValue,
        _ => track.TitleSortValue,
    };

    public string? AlbumGridLetterText(AlbumTileViewModel tile) => Main.AlbumGridLetterText(tile);

    // The sort in use, picked again, turns round (BrowseSort.Pick).
    private void ChooseSort(BrowseSortScreen screen, BrowseSortOrder order)
    {
        Main.ChooseSort(screen, order);
        switch (screen)
        {
            case BrowseSortScreen.Songs:
                UseTheSortThisScreenWants(flatSongs: true);
                Main.RebuildRowsImmediatelyAsync(includeGridTiles: false).Forget(_logger, "Songs re-sort");
                OnPropertyChanged(nameof(SongsAreAlphabetical));
                OnPropertyChanged(nameof(SongsRunZToA));
                break;
            case BrowseSortScreen.Albums:
                RebuildAlbumGrid();
                OnPropertyChanged(nameof(AlbumGridIsAlphabetical));
                OnPropertyChanged(nameof(AlbumGridRunsZToA));
                break;
            case BrowseSortScreen.Artists:
                RebuildArtistPickerRows();
                OnPropertyChanged(nameof(ArtistPickerIsAlphabetical));
                OnPropertyChanged(nameof(ArtistPickerRunsZToA));
                break;
            case BrowseSortScreen.ArtistAlbums:
                RebuildArtistAlbumGrid();
                break;
        }
    }

    // Every song the screen shows, for its Shuffle: what is on screen, so a
    // screen with its pull-down filter open shuffles only what the filter
    // left. A list of songs is those songs; a grid is every song on its
    // albums; the artist and playlist lists are every song by the artists, or
    // in the playlists, listed. Search is every song the query finds, not
    // only the few its songs section has room for.
    private IReadOnlyList<Track> TracksOnScreen(MobileNavigationFrame frame) => frame.ScreenKind switch
    {
        MobileScreenKind.TrackList => Main.Rows.Select(r => r.Track).ToList(),
        MobileScreenKind.AlbumGrid => TracksOf(_albumGrid),
        MobileScreenKind.ArtistAlbumGrid => TracksOf(_artistAlbumGrid),
        MobileScreenKind.ArtistPicker => ArtistPickerItems.Select(r => r.Name).ToHashSet(ArtistNames.Comparer) is var artists
            ? Main.Library.Tracks.Where(t => t.Artists != null && artists.Contains(t.Artists)).ToList()
            : [],
        // A song in two of the playlists is still one song.
        MobileScreenKind.PlaylistPicker => PlaylistPickerItems
            .SelectMany(i => i.Playlist?.Tracks ?? [])
            .Distinct()
            .ToList(),
        MobileScreenKind.SearchResults => string.IsNullOrWhiteSpace(Search.Query)
            ? []
            : Main.Library.Tracks.Where(t => TrackListBuilder.Matches(t, Search.Query)).ToList(),
        _ => [],
    };

    private static List<Track> TracksOf(FilterableAlbumGrid grid) =>
        grid.Rows.SelectMany(r => r.Tiles).SelectMany(t => t.Tracks).ToList();

    // What takes songs off this device, in a section of its own above
    // Settings, each through the same confirmation its song and album
    // counterparts use - which is where the count is spelled out, and so the
    // guard against clearing a whole library from the Songs tab by mistake.
    //
    // Delete Local Files once anything on screen has a file here. Remove from
    // Library wherever the album menu would offer it: not on a playlist, or
    // the list of them, for the reason ActionMenus.ForAlbum gives - there it
    // reads as deleting the playlist, and takes every song
    // in it out of the library instead.
    private List<ScreenMenuEntry> DestructiveEntries(MobileNavigationFrame frame)
    {
        var entries = new List<ScreenMenuEntry>();
        var tracks = TracksOnScreen(frame);

        if (tracks.Any(t => t.Path != null))
            entries.Add(new ScreenMenuEntry("Delete Local Files", MaterialIconKind.TrashCanOutline, () =>
            {
                var files = TracksOnScreen(frame).Where(t => t.Path != null).ToList();
                if (files.Count > 0)
                    ConfirmDeleting(files, of: ScreenName(frame));
            }, IsDestructive: true));

        var onAPlaylist = frame.IsPlaylistTrackList || frame.ScreenKind == MobileScreenKind.PlaylistPicker;
        if (!onAPlaylist && _libraryRemoval?.Removable(tracks).Count > 0)
            entries.Add(new ScreenMenuEntry("Remove from Library", MaterialIconKind.MusicNoteOff,
                () => ConfirmRemovingFromLibrary(TracksOnScreen(frame)), IsDestructive: true));

        return entries;
    }

    // What the delete confirmation calls the screen's songs: the album's name
    // over an album, which has no title of its own (see MobileNavigationFrame
    // .Title), and the screen's title elsewhere.
    private string? ScreenName(MobileNavigationFrame frame) =>
        frame.IsAlbumTrackList ? CurrentAlbumHeader?.Name
        : string.IsNullOrEmpty(frame.Title) ? null
        : frame.Title;

    // Play Next and Add to Queue over a screen's songs, worked out when picked.
    private IEnumerable<ScreenMenuEntry> QueueEntries(Func<IReadOnlyList<Track>> tracks) =>
    [
        new ScreenMenuEntry("Play Next", MaterialIconKind.PlaylistPlay, () => LineUp(tracks(), next: true)),
        new ScreenMenuEntry("Add to Queue", MaterialIconKind.PlaylistMusicOutline, () => LineUp(tracks(), next: false)),
    ];

    /// <summary>
    /// The header menu for <paramref name="frame"/>'s screen, built as it
    /// opens. Only ever asked of the live screen - the one kept behind it for a
    /// swipe cannot be tapped - so the actions act on the live state.
    /// </summary>
    public IReadOnlyList<ScreenMenuSection> BuildScreenMenu(MobileNavigationFrame frame)
    {
        var sections = new List<ScreenMenuSection>();

        if (SortScreenFor(frame) is { } screen)
        {
            var (current, ascending) = SortOf(screen);
            sections.Add(new ScreenMenuSection("Sort By", BrowseSort.SortsFor(screen)
                .Select(order => new ScreenMenuEntry(BrowseSort.LabelFor(order), BrowseSort.IconFor(order),
                    () => ChooseSort(screen, order),
                    Direction: order != current ? null
                        : ascending ? ListSortDirection.Ascending : ListSortDirection.Descending))
                .ToList()));
        }

        // Download All goes last, right above Settings, wherever it appears.
        var actions = new List<ScreenMenuEntry>();
        ScreenMenuEntry? downloadAll = null;
        // A playlist's pencil, first: the one thing its screen offers that an
        // album's does not - its name if it is a plain one, its rules if it is
        // smart (EditCurrentPlaylistCommand decides which).
        if (frame.IsPlaylistTrackList && CanEditCurrentPlaylist)
            actions.Add(new ScreenMenuEntry("Edit", MaterialIconKind.PencilOutline,
                () => EditCurrentPlaylistCommand.Execute(null)));
        if (TracksOnScreen(frame).Count > 0)
            actions.Add(new ScreenMenuEntry("Shuffle", MaterialIconKind.Shuffle,
                () => Main.PlayTracks(TracksOnScreen(frame), shuffle: true)));
        if (frame.IsTrackList && Main.Rows.Count > 0)
        {
            // The album or playlist on screen, in the order it shows.
            actions.AddRange(QueueEntries(() => CurrentTrackRows.Select(r => r.Track).ToList()));
            actions.Add(new ScreenMenuEntry("Add to Playlist", MaterialIconKind.PlaylistPlus,
                () => OpenAlbumAddToPlaylistCommand.Execute(null)));
            // Every song of the album at once - its common tags, and what is
            // typed applied to them all.
            if (frame.IsAlbumTrackList)
                actions.Add(new ScreenMenuEntry("Info", MaterialIconKind.InformationOutline,
                    () => OpenTrackInfo(CurrentTrackRows.Select(r => r.Track).ToList())));
            if (Main.CanForceSync && DownloadAllIndicator.IsDownloadable && !DownloadAllIndicator.IsDownloading)
                downloadAll = new ScreenMenuEntry("Download All", MaterialIconKind.CloudDownloadOutline,
                    () => DownloadAllVisibleCommand.Execute(null));
        }
        // An artist's albums, all of them: the same two the long press on
        // their name offers, through the same tile that press builds.
        else if (frame.ScreenKind == MobileScreenKind.ArtistAlbumGrid
                 && frame.SelectedArtistName is { } artist
                 && BuildArtistTile(artist) is { } tile)
        {
            actions.AddRange(QueueEntries(() => InAlbumOrder(tile)));
            actions.Add(new ScreenMenuEntry("Add to Playlist", MaterialIconKind.PlaylistPlus, () =>
            {
                ActionTarget = null;
                AlbumActionTarget = tile;
                AddAlbumActionTargetToPlaylistCommand.Execute(null);
            }));
            if (Main.CanForceSync && tile.IsDownloadable)
                downloadAll = new ScreenMenuEntry("Download All", MaterialIconKind.CloudDownloadOutline, () =>
                {
                    AlbumActionTarget = tile;
                    DownloadAlbumActionTargetCommand.Execute(null);
                });
        }
        if (downloadAll != null)
            actions.Add(downloadAll);

        // The past searches the box finishes words from.
        if (frame.IsSearchScreen && Main.RecentSearches.Count > 0)
            actions.Add(new ScreenMenuEntry("Clear Recent Searches", MaterialIconKind.History,
                () => Search.ClearRecentSearchesCommand.Execute(null)));

        // Everything queued but the song playing.
        if (frame.ScreenKind == MobileScreenKind.Queue && PlaylistControl.CanClearQueue)
            actions.Add(new ScreenMenuEntry("Clear Queue", MaterialIconKind.PlaylistRemove,
                () => PlaylistControl.ClearQueue(), IsDestructive: true));
        if (actions.Count > 0)
            sections.Add(new ScreenMenuSection(null, actions));

        var destructive = DestructiveEntries(frame);
        if (destructive.Count > 0)
            sections.Add(new ScreenMenuSection(null, destructive));

        sections.Add(new ScreenMenuSection(null,
            [new ScreenMenuEntry("Settings", MaterialIconKind.CogOutline, () => OpenSettingsCommand.Execute(null))]));
        return sections;
    }

    /// <summary>
    /// The Now Playing sheet's menu - the same hamburger every screen has, for
    /// the song playing: where it is from (its album and its artist, which a
    /// tap on either name on the sheet also goes to), what can be done with it,
    /// and Settings last, as everywhere.
    /// </summary>
    public IReadOnlyList<ScreenMenuSection> BuildNowPlayingMenu()
    {
        var sections = new List<ScreenMenuSection>();
        if (PlaylistControl.CurrentlyPlayingTrack is { } playing)
        {
            var goTo = new List<ScreenMenuEntry>();
            if (!string.IsNullOrEmpty(playing.Album))
                goTo.Add(new ScreenMenuEntry("Go to Album", MaterialIconKind.Album,
                    () => GoToCurrentlyPlayingAlbumCommand.Execute(null)));
            if (!string.IsNullOrEmpty(playing.Artists))
                goTo.Add(new ScreenMenuEntry("Go to Artist", MaterialIconKind.AccountMusicOutline,
                    () => GoToCurrentlyPlayingArtistCommand.Execute(null)));
            if (goTo.Count > 0)
                sections.Add(new ScreenMenuSection(null, goTo));

            sections.Add(new ScreenMenuSection(null,
            [
                new ScreenMenuEntry("Add to Playlist", MaterialIconKind.PlaylistPlus,
                    () => OpenSongSheetForPlaying(MobileSheet.AddToPlaylist)),
                new ScreenMenuEntry("Show Queue", MaterialIconKind.PlaylistMusicOutline,
                    () => SelectTabCommand.Execute(nameof(MobileTab.Queue))),
                new ScreenMenuEntry("Info", MaterialIconKind.InformationOutline,
                    () => OpenSongSheetForPlaying(MobileSheet.TrackInfo)),
            ]));
        }

        sections.Add(new ScreenMenuSection(null,
            [new ScreenMenuEntry("Settings", MaterialIconKind.CogOutline, () => OpenSettingsCommand.Execute(null))]));
        return sections;
    }

    // A song's sheet - Add to Playlist, Track Info - for the song playing,
    // as a row's "..." menu opens it for that row's. On the library's own
    // track rather than the one playing: a streamed song plays as a copy that
    // carries its stream address (same Id), and an edit or a playlist entry
    // belongs to the song in the library, not to that copy.
    private void OpenSongSheetForPlaying(MobileSheet sheet)
    {
        if (PlaylistControl.CurrentlyPlayingTrack is not { } playing)
            return;
        _actionRow = null;
        _playlistTargets = null;
        ActionTarget = Main.Library.Snapshot.ById.GetValueOrDefault(playing.Id) ?? playing;
        if (sheet == MobileSheet.TrackInfo)
            OpenTrackInfo([ActionTarget]);
        else
            ActiveSheet = sheet;
    }
}
