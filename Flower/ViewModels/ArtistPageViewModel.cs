using System;
using System.Collections.Generic;
using System.Linq;

using Flower.Models;
using Flower.Services;
using Flower.ViewModels.Mobile;

namespace Flower.ViewModels;

// The desktop Artists page, for the one artist picked: who they are, the way
// the phone's artist list draws one (ArtistPickerRow - their covers as a
// collage, how many albums and songs) and how long it all runs, and then each
// of their albums as a card of its own with its songs under it (Albums).
// Several artists picked at once for a drag have no one name to head them, so
// the page shows nobody. Built when the pick or the library changes rather
// than on every read, so the covers behind it are fetched once per artist.
//
// Its own class rather than a region of MainViewModel
// (docs/CODE-REVIEW-2026-09.md E3), which tells it who is picked (Show) and
// how songs should draw (UpdateRows).
public sealed class ArtistPageViewModel : ViewModelBase
{
    public ArtistHeaderInfo? Header { get; private set; }

    public IReadOnlyList<ArtistAlbumSectionViewModel> Albums { get; private set; } = [];

    // Every song on the page, in the cards' order - what the page header's
    // play and shuffle circles queue.
    public List<Track> AllTracks => Albums.SelectMany(a => a.ExpandedTracks).ToList();

    // name is the one artist picked, or null for none (or several). force:
    // the library changed under the page, so everything is rebuilt. The filter
    // does not reach the cards: it finds artists by name, in the column beside
    // them, and the page is whichever one is picked, whole.
    public void Show(string? name, IReadOnlyList<Track> library, TrackAvailabilityContext availability,
        Guid? playing, (BrowseSortOrder Order, bool Ascending) sort, bool force = false)
    {
        if (!force && name == Header?.Name)
            return;

        foreach (var section in Albums)
        {
            section.SelectionStarted -= Section_SelectionStarted;
            section.ExpandedTracks = [];
            section.Tile.Dispose();
        }

        if (name == null)
        {
            Header = null;
            Albums = [];
        }
        else
        {
            var tracks = library.Where(t => ArtistNames.Same(t.Artists, name)).ToList();
            var albums = tracks.Where(t => !string.IsNullOrEmpty(t.Album)).Select(t => t.Album).Distinct().Count();
            var total = TimeSpan.FromTicks(tracks.Sum(t => t.Duration.Ticks));
            var length = total.TotalHours >= 1 ? $"{(int)total.TotalHours}h {total.Minutes}m" : $"{total.Minutes}m";
            Header = new ArtistHeaderInfo(name, AlbumCollage.ByAlbumName(tracks),
                $"{ArtistPickerRow.Summary(albums, tracks.Count)} · {length}");

            // In the order picked from the page's Sort button - oldest first
            // until one is (BrowseSort.DefaultFor).
            var tiles = BrowseSort.InGridOrder(AlbumGridBuilder.Build(tracks), sort);
            // Songs on no album still belong to the artist: they go in a card
            // of their own, last.
            var loose = tracks.Where(t => string.IsNullOrEmpty(t.Album)).ToList();
            if (loose.Count > 0)
                tiles.Add(new AlbumTileViewModel
                {
                    Name = "Other Songs",
                    Artist = name,
                    RepresentativeTrack = loose[0],
                    MostRecentlyAdded = loose.Max(t => t.DateAdded),
                    Tracks = loose,
                });

            Albums = tiles.Select(tile =>
            {
                var section = new ArtistAlbumSectionViewModel(tile)
                {
                    Availability = availability,
                    CurrentlyPlayingTrackId = playing,
                };
                section.ExpandedTracks = LibraryBrowserViewModel.ExpandedTracksFor(tile);
                section.SelectionStarted += Section_SelectionStarted;
                return section;
            }).ToList();
        }
        OnPropertyChanged(nameof(Header));
        OnPropertyChanged(nameof(Albums));
    }

    // A song starting, or the server coming and going, changes how the cards'
    // songs draw - the same two things AlbumGridView is told about.
    public void UpdateRows(TrackAvailabilityContext availability, Guid? playing)
    {
        foreach (var section in Albums)
        {
            section.Availability = availability;
            section.CurrentlyPlayingTrackId = playing;
        }
    }

    // One selection across the page, as one list would have.
    private void Section_SelectionStarted(object? sender, EventArgs e)
    {
        foreach (var section in Albums)
        {
            if (!ReferenceEquals(section, sender))
                section.ClearSelection();
        }
    }
}
