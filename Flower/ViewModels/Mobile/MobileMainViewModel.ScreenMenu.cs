using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;

using Material.Icons;

using Flower.Models;
using Flower.Services;

namespace Flower.ViewModels.Mobile;

// The orders a phone screen can be put in from its header menu. Not every
// screen offers every one - see MobileMainViewModel.SortsFor.
public enum MobileSortOrder { Name, Artist, Album, Year, DateAdded, MostPlayed }

// The screens that can be sorted at all, and so the keys their choice is kept
// under (AppSettings.MobileSorts). Everything else comes in an order that is
// the point of it: an album is its track order, a playlist is the order it
// was made in, and Home's shelves are most recent first by definition.
public enum MobileSortScreen { Songs, Albums, ArtistAlbums, Artists }

// One row of a screen's header menu (ScreenSlot's hamburger). A plain action
// rather than a command: the menu is built for the screen it is opened on, at
// the moment it is opened, so nothing about it has to stay live.
//
// Direction is set on the one sort entry in use, and says which way it runs;
// null on every other entry. IsDestructive draws it in red.
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

    private static readonly MobileSortOrder[] SongSorts =
        [MobileSortOrder.Name, MobileSortOrder.Artist, MobileSortOrder.Album, MobileSortOrder.Year, MobileSortOrder.DateAdded];
    private static readonly MobileSortOrder[] AlbumSorts =
        [MobileSortOrder.Name, MobileSortOrder.Artist, MobileSortOrder.Year, MobileSortOrder.DateAdded];
    // Every album here is by the one artist the screen is for.
    private static readonly MobileSortOrder[] ArtistAlbumSorts =
        [MobileSortOrder.Name, MobileSortOrder.Year, MobileSortOrder.DateAdded];
    // An artist has no one year, but they do have a newest song and a count
    // of how often theirs have been played.
    private static readonly MobileSortOrder[] ArtistSorts =
        [MobileSortOrder.Name, MobileSortOrder.MostPlayed, MobileSortOrder.DateAdded];

    private static IReadOnlyList<MobileSortOrder> SortsFor(MobileSortScreen screen) => screen switch
    {
        MobileSortScreen.Songs => SongSorts,
        MobileSortScreen.Albums => AlbumSorts,
        MobileSortScreen.Artists => ArtistSorts,
        _ => ArtistAlbumSorts,
    };

    private static MobileSortScreen? SortScreenFor(MobileNavigationFrame frame) => frame.ScreenKind switch
    {
        MobileScreenKind.TrackList when frame.Tab == MobileTab.Songs && !frame.HasDrilledIn => MobileSortScreen.Songs,
        MobileScreenKind.AlbumGrid => MobileSortScreen.Albums,
        MobileScreenKind.ArtistAlbumGrid => MobileSortScreen.ArtistAlbums,
        MobileScreenKind.ArtistPicker => MobileSortScreen.Artists,
        _ => null,
    };

    // Read through to settings every time rather than cached: it is asked on
    // a rebuild and on a menu opening, neither of which is a hot path. Kept
    // as "Order:asc" or "Order:desc". Name, its own way round, when nothing
    // was ever picked or what was picked is no longer one this screen offers.
    private (MobileSortOrder Order, bool Ascending) SortOf(MobileSortScreen screen)
    {
        var saved = Main.MobileSortFor(screen.ToString())?.Split(':');
        if (saved is [var name, var direction]
            && Enum.TryParse<MobileSortOrder>(name, out var order)
            && SortsFor(screen).Contains(order))
            return (order, direction != "desc");
        return (MobileSortOrder.Name, NaturallyAscending(MobileSortOrder.Name));
    }

    // Which way an order runs when it is first picked: names from A, years
    // oldest first, which is how a library reads as a history, and Date Added
    // and Most Played from the top, the only way anyone asks for either.
    // Picking it again turns it round (ChooseSort).
    private static bool NaturallyAscending(MobileSortOrder order) =>
        order is not (MobileSortOrder.DateAdded or MobileSortOrder.MostPlayed);

    private static string LabelFor(MobileSortOrder order) => order switch
    {
        MobileSortOrder.Name => "Name",
        MobileSortOrder.Artist => "Artist",
        MobileSortOrder.Album => "Album",
        MobileSortOrder.Year => "Year",
        MobileSortOrder.MostPlayed => "Most Played",
        _ => "Date Added",
    };

    private static MaterialIconKind IconFor(MobileSortOrder order) => order switch
    {
        MobileSortOrder.Name => MaterialIconKind.SortAlphabeticalAscending,
        MobileSortOrder.Artist => MaterialIconKind.AccountMusicOutline,
        MobileSortOrder.Album => MaterialIconKind.Album,
        MobileSortOrder.Year => MaterialIconKind.CalendarBlankOutline,
        MobileSortOrder.MostPlayed => MaterialIconKind.TrendingUp,
        _ => MaterialIconKind.ClockOutline,
    };

    // The flat Songs list's order as TrackListBuilder names it.
    private (string Column, bool Ascending) SongsColumn()
    {
        var (order, ascending) = SortOf(MobileSortScreen.Songs);
        var column = order switch
        {
            MobileSortOrder.Artist => "Artist",
            MobileSortOrder.Album => "Album",
            MobileSortOrder.Year => "Year",
            MobileSortOrder.DateAdded => "DateAdded",
            _ => "Title",
        };
        return (column, ascending);
    }

    // An album grid in the order its screen was given. Only the order's own
    // key turns round: albums that tie on it stay alphabetical either way, and
    // albums with no year go last either way.
    private List<AlbumTileViewModel> InGridOrder(List<AlbumTileViewModel> tiles, MobileSortScreen screen)
    {
        var (order, ascending) = SortOf(screen);
        return order switch
        {
            MobileSortOrder.Artist => Order(tiles, t => TrackListBuilder.SortKey(t.Artist), ascending)
                .ThenBy(t => TrackListBuilder.SortKey(t.Name)).ToList(),
            MobileSortOrder.Year => tiles.OrderBy(t => YearOf(t) == null)
                .ThenBy(t => YearOf(t), ascending ? Comparer<string?>.Default : Comparer<string?>.Create((x, y) => string.CompareOrdinal(y, x)))
                .ThenBy(t => TrackListBuilder.SortKey(t.Name)).ToList(),
            MobileSortOrder.DateAdded => Order(tiles, t => t.MostRecentlyAdded, ascending).ToList(),
            _ => Order(tiles, t => TrackListBuilder.SortKey(t.Name), ascending).ToList(),
        };
    }

    private static IOrderedEnumerable<T> Order<T, TKey>(
        IEnumerable<T> items, Func<T, TKey> key, bool ascending) =>
        ascending ? items.OrderBy(key) : items.OrderByDescending(key);

    // The artist list in the order its screen was given. Names come in
    // alphabetical already (SubListItems), so only turning them round is
    // work; the other two keep that order among artists that tie.
    private List<ArtistPickerRow> InArtistOrder(List<ArtistPickerRow> rows, Dictionary<string, List<Track>> byArtist)
    {
        var (order, ascending) = SortOf(MobileSortScreen.Artists);
        IEnumerable<Track> TracksOf(ArtistPickerRow row) => byArtist.GetValueOrDefault(row.Name) ?? [];
        return order switch
        {
            MobileSortOrder.MostPlayed => Order(rows, r => TracksOf(r).Sum(t => t.TotalPlayCount), ascending).ToList(),
            MobileSortOrder.DateAdded => Order(rows, r => TracksOf(r).Select(t => t.DateAdded).DefaultIfEmpty().Max(), ascending).ToList(),
            _ when !ascending => Enumerable.Reverse(rows).ToList(),
            _ => rows,
        };
    }

    // Whether a screen's list is sorted on something with a letter, and so
    // whether its index bar can be letters (ScrollIndexBar.ShowsLetters). Any
    // other order - a year, a date, a count - has no letter to jump to, and
    // the bar is dots, a scrubber over the whole scroll. The ZToA pair turns
    // the bar round with a reversed sort (ScrollIndexBar.LettersDescending).
    public bool AlbumGridIsAlphabetical =>
        SortOf(MobileSortScreen.Albums).Order is MobileSortOrder.Name or MobileSortOrder.Artist;

    public bool AlbumGridRunsZToA => AlbumGridIsAlphabetical && !SortOf(MobileSortScreen.Albums).Ascending;

    public bool ArtistPickerIsAlphabetical =>
        SortOf(MobileSortScreen.Artists).Order == MobileSortOrder.Name;

    public bool ArtistPickerRunsZToA => ArtistPickerIsAlphabetical && !SortOf(MobileSortScreen.Artists).Ascending;

    public bool SongsAreAlphabetical =>
        SortOf(MobileSortScreen.Songs).Order is MobileSortOrder.Name or MobileSortOrder.Artist or MobileSortOrder.Album;

    public bool SongsRunZToA => SongsAreAlphabetical && !SortOf(MobileSortScreen.Songs).Ascending;

    // What a song is filed under on the bar: the sort-as value of whatever the
    // list is sorted on, as TrackListBuilder sorts it.
    public string? SongLetterText(Track track) => SortOf(MobileSortScreen.Songs).Order switch
    {
        MobileSortOrder.Artist => track.ArtistsSortValue,
        MobileSortOrder.Album => track.AlbumSortValue,
        _ => track.TitleSortValue,
    };

    // What an album on the grid is filed under on the bar: its artist when the
    // grid is sorted by artist, its own name otherwise.
    public string? AlbumGridLetterText(AlbumTileViewModel tile) =>
        SortOf(MobileSortScreen.Albums).Order == MobileSortOrder.Artist ? tile.Artist : tile.Name;

    // An album's earliest year - a reissue's bonus tracks carry the reissue's.
    private static string? YearOf(AlbumTileViewModel tile) =>
        tile.Tracks.Select(t => t.Year).Where(y => !string.IsNullOrWhiteSpace(y)).Min();

    // The sort in use, picked again, turns round; any other starts its own
    // natural way round.
    private void ChooseSort(MobileSortScreen screen, MobileSortOrder order)
    {
        var (current, ascending) = SortOf(screen);
        var nowAscending = order == current ? !ascending : NaturallyAscending(order);
        Main.PersistMobileSort(screen.ToString(), $"{order}:{(nowAscending ? "asc" : "desc")}");
        switch (screen)
        {
            case MobileSortScreen.Songs:
                UseTheSortThisScreenWants(flatSongs: true);
                Main.RebuildRowsImmediatelyAsync(includeGridTiles: false).Forget(_logger, "Songs re-sort");
                OnPropertyChanged(nameof(SongsAreAlphabetical));
                OnPropertyChanged(nameof(SongsRunZToA));
                break;
            case MobileSortScreen.Albums:
                RebuildAlbumGrid();
                OnPropertyChanged(nameof(AlbumGridIsAlphabetical));
                OnPropertyChanged(nameof(AlbumGridRunsZToA));
                break;
            case MobileSortScreen.Artists:
                RebuildArtistPickerRows();
                OnPropertyChanged(nameof(ArtistPickerIsAlphabetical));
                OnPropertyChanged(nameof(ArtistPickerRunsZToA));
                break;
            case MobileSortScreen.ArtistAlbums:
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
        MobileScreenKind.ArtistPicker => ArtistPickerItems.Select(r => r.Name).ToHashSet() is var artists
            ? Main.Library.Tracks.Where(t => t.Artists != null && artists.Contains(t.Artists)).ToList()
            : [],
        // A song in two of the playlists is still one song.
        MobileScreenKind.PlaylistPicker => PlaylistPickerItems
            .SelectMany(i => i.Playlist?.Tracks ?? [])
            .Distinct()
            .ToList(),
        MobileScreenKind.SearchResults => string.IsNullOrWhiteSpace(SearchQuery)
            ? []
            : Main.Library.Tracks.Where(t => TrackListBuilder.Matches(t, SearchQuery)).ToList(),
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
    // the list of them, for the reason CanRemoveAlbumActionTargetFromLibrary
    // gives - there it reads as deleting the playlist, and takes every song
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
        new ScreenMenuEntry("Add to Queue", MaterialIconKind.PlaylistMusic, () => LineUp(tracks(), next: false)),
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
            sections.Add(new ScreenMenuSection("Sort By", SortsFor(screen)
                .Select(order => new ScreenMenuEntry(LabelFor(order), IconFor(order),
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
            actions.Add(new ScreenMenuEntry("Edit", MaterialIconKind.Pencil,
                () => EditCurrentPlaylistCommand.Execute(null)));
        if (TracksOnScreen(frame).Count > 0)
            actions.Add(new ScreenMenuEntry("Shuffle", MaterialIconKind.Shuffle,
                () => PlayTracks(TracksOnScreen(frame), shuffle: true)));
        if (frame.IsTrackList && Main.Rows.Count > 0)
        {
            // The album or playlist on screen, in the order it shows.
            actions.AddRange(QueueEntries(() => CurrentTrackRows.Select(r => r.Track).ToList()));
            actions.Add(new ScreenMenuEntry("Add to Playlist", MaterialIconKind.PlaylistPlus,
                () => OpenAlbumAddToPlaylistCommand.Execute(null)));
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
        if (actions.Count > 0)
            sections.Add(new ScreenMenuSection(null, actions));

        var destructive = DestructiveEntries(frame);
        if (destructive.Count > 0)
            sections.Add(new ScreenMenuSection(null, destructive));

        sections.Add(new ScreenMenuSection(null,
            [new ScreenMenuEntry("Settings", MaterialIconKind.Cog, () => OpenSettingsCommand.Execute(null))]));
        return sections;
    }
}
