using System;
using System.Linq;

using Flower.ViewModels.Mobile;

namespace Flower.ViewModels;

// One album on the desktop Artists page (ArtistPageViewModel.Albums): its
// card's cover, name and line about it, and its songs as a list of their own
// (AlbumTracksViewModel, drawn by AlbumTrackList).
public sealed class ArtistAlbumSectionViewModel : AlbumTracksViewModel
{
    public ArtistAlbumSectionViewModel(AlbumTileViewModel tile)
    {
        Tile = tile;
        var year = BrowseSort.YearOf(tile);
        var total = TimeSpan.FromTicks(tile.Tracks.Sum(t => t.Duration.Ticks));
        var length = total.TotalHours >= 1 ? $"{(int)total.TotalHours}h {total.Minutes}m" : $"{(int)total.TotalMinutes}m";
        var songs = tile.Tracks.Count == 1 ? "1 song" : $"{tile.Tracks.Count} songs";
        Meta = string.Join(" · ", new[] { tile.Artist, year, songs, length }.Where(s => !string.IsNullOrEmpty(s)));
    }

    public AlbumTileViewModel Tile { get; }

    // "SAINT PEPSI · 2013 · 13 songs · 36m".
    public string Meta { get; }
}
