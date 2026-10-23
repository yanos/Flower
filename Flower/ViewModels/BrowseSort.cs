using System;
using System.Collections.Generic;
using System.Linq;

using Material.Icons;

using Flower.Models;
using Flower.Services;
using Flower.ViewModels.Mobile;

namespace Flower.ViewModels;

// The orders a screen of albums or artists can be put in - a phone screen's
// header menu, the desktop's Sort button over the Albums grid and the Artists
// page. Not every screen offers every one - see BrowseSort.SortsFor.
public enum BrowseSortOrder { Name, Artist, Album, Year, DateAdded, MostPlayed }

// The screens that can be sorted at all, and so the keys their choice is kept
// under (AppSettings.ScreenSorts). Everything else comes in an order that is
// the point of it: an album is its track order, a playlist is the order it
// was made in, and Home's shelves are most recent first by definition. Songs
// is the phone's alone: the desktop's track list sorts by its column headers.
public enum BrowseSortScreen { Songs, Albums, ArtistAlbums, Artists }

// What each order is, once for both heads: which screens offer which, what
// they are called, which way each runs when first picked, and the ordering
// itself. Where the choice is kept is MainViewModel's (SortOf, ChooseSort).
public static class BrowseSort
{
    private static readonly BrowseSortOrder[] SongSorts =
        [BrowseSortOrder.Name, BrowseSortOrder.Artist, BrowseSortOrder.Album, BrowseSortOrder.Year, BrowseSortOrder.DateAdded];
    private static readonly BrowseSortOrder[] AlbumSorts =
        [BrowseSortOrder.Name, BrowseSortOrder.Artist, BrowseSortOrder.Year, BrowseSortOrder.DateAdded];
    // Every album here is by the one artist the screen is for.
    private static readonly BrowseSortOrder[] ArtistAlbumSorts =
        [BrowseSortOrder.Name, BrowseSortOrder.Year, BrowseSortOrder.DateAdded];
    // An artist has no one year, but they do have a newest song and a count
    // of how often theirs have been played.
    private static readonly BrowseSortOrder[] ArtistSorts =
        [BrowseSortOrder.Name, BrowseSortOrder.MostPlayed, BrowseSortOrder.DateAdded];

    public static IReadOnlyList<BrowseSortOrder> SortsFor(BrowseSortScreen screen) => screen switch
    {
        BrowseSortScreen.Songs => SongSorts,
        BrowseSortScreen.Albums => AlbumSorts,
        BrowseSortScreen.Artists => ArtistSorts,
        _ => ArtistAlbumSorts,
    };

    // What a screen is in when nothing was ever picked for it: an artist's
    // albums oldest first, which is how a career reads and what the desktop's
    // Artists page always did; everything else by name.
    public static (BrowseSortOrder Order, bool Ascending) DefaultFor(BrowseSortScreen screen) =>
        screen == BrowseSortScreen.ArtistAlbums ? (BrowseSortOrder.Year, true) : (BrowseSortOrder.Name, true);

    // Kept as "Order:asc" or "Order:desc". The screen's default when nothing
    // was ever picked, or what was picked is no longer one it offers.
    public static (BrowseSortOrder Order, bool Ascending) Parse(string? saved, BrowseSortScreen screen)
    {
        if (saved?.Split(':') is [var name, var direction]
            && Enum.TryParse<BrowseSortOrder>(name, out var order)
            && SortsFor(screen).Contains(order))
            return (order, direction != "desc");
        return DefaultFor(screen);
    }

    public static string Format(BrowseSortOrder order, bool ascending) =>
        $"{order}:{(ascending ? "asc" : "desc")}";

    // The sort in use, picked again, turns round; any other starts its own
    // natural way round.
    public static (BrowseSortOrder Order, bool Ascending) Pick(
        (BrowseSortOrder Order, bool Ascending) current, BrowseSortOrder picked) =>
        (picked, picked == current.Order ? !current.Ascending : NaturallyAscending(picked));

    // Which way an order runs when it is first picked: names from A, years
    // oldest first, which is how a library reads as a history, and Date Added
    // and Most Played from the top, the only way anyone asks for either.
    public static bool NaturallyAscending(BrowseSortOrder order) =>
        order is not (BrowseSortOrder.DateAdded or BrowseSortOrder.MostPlayed);

    public static string LabelFor(BrowseSortOrder order) => order switch
    {
        BrowseSortOrder.Name => "Name",
        BrowseSortOrder.Artist => "Artist",
        BrowseSortOrder.Album => "Album",
        BrowseSortOrder.Year => "Year",
        BrowseSortOrder.MostPlayed => "Most Played",
        _ => "Date Added",
    };

    public static MaterialIconKind IconFor(BrowseSortOrder order) => order switch
    {
        BrowseSortOrder.Name => MaterialIconKind.SortAlphabeticalAscending,
        BrowseSortOrder.Artist => MaterialIconKind.AccountMusicOutline,
        BrowseSortOrder.Album => MaterialIconKind.Album,
        BrowseSortOrder.Year => MaterialIconKind.CalendarBlankOutline,
        BrowseSortOrder.MostPlayed => MaterialIconKind.TrendingUp,
        _ => MaterialIconKind.ClockOutline,
    };

    // Albums in an order. Only the order's own key turns round: albums that
    // tie on it stay alphabetical either way, and albums with no year go last
    // either way.
    public static List<AlbumTileViewModel> InGridOrder(
        IEnumerable<AlbumTileViewModel> tiles, (BrowseSortOrder Order, bool Ascending) sort)
    {
        var (order, ascending) = sort;
        return order switch
        {
            BrowseSortOrder.Artist => Order(tiles, t => TrackListBuilder.SortKey(t.Artist), ascending)
                .ThenBy(t => TrackListBuilder.SortKey(t.Name)).ToList(),
            BrowseSortOrder.Year => tiles.OrderBy(t => YearOf(t) == null)
                .ThenBy(t => YearOf(t), ascending ? Comparer<string?>.Default : Comparer<string?>.Create((x, y) => string.CompareOrdinal(y, x)))
                .ThenBy(t => TrackListBuilder.SortKey(t.Name)).ToList(),
            BrowseSortOrder.DateAdded => Order(tiles, t => t.MostRecentlyAdded, ascending).ToList(),
            _ => Order(tiles, t => TrackListBuilder.SortKey(t.Name), ascending).ToList(),
        };
    }

    // Artists in an order, from a list that is alphabetical already (as
    // ArtistNames.Of makes it) - so only turning names round is work; the
    // other two keep that order among artists that tie. tracksOf is each
    // artist's songs.
    public static List<T> InArtistOrder<T>(
        IReadOnlyList<T> artists, Func<T, IEnumerable<Track>> tracksOf, (BrowseSortOrder Order, bool Ascending) sort)
    {
        var (order, ascending) = sort;
        return order switch
        {
            BrowseSortOrder.MostPlayed => Order(artists, a => tracksOf(a).Sum(t => t.TotalPlayCount), ascending).ToList(),
            BrowseSortOrder.DateAdded => Order(artists, a => tracksOf(a).Select(t => t.DateAdded).DefaultIfEmpty().Max(), ascending).ToList(),
            _ when !ascending => Enumerable.Reverse(artists).ToList(),
            _ => artists.ToList(),
        };
    }

    // Every song by each artist named, keyed the way the Artists lists name
    // them - for InArtistOrder.
    public static Dictionary<string, List<Track>> TracksByArtist(IEnumerable<Track> library) =>
        library
            .Where(t => t.Artists != null)
            .GroupBy(t => t.Artists!, ArtistNames.Comparer)
            .ToDictionary(g => g.Key, g => g.ToList(), ArtistNames.Comparer);

    // An album's earliest year - a reissue's bonus tracks carry the reissue's.
    public static string? YearOf(AlbumTileViewModel tile) =>
        tile.Tracks.Select(t => t.Year).Where(y => !string.IsNullOrWhiteSpace(y)).Min();

    private static IOrderedEnumerable<T> Order<T, TKey>(
        IEnumerable<T> items, Func<T, TKey> key, bool ascending) =>
        ascending ? items.OrderBy(key) : items.OrderByDescending(key);
}
