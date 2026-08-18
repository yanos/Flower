using System;
using System.Collections.Generic;
using System.Linq;

using Flower.Models;

namespace Flower.ViewModels;

/// <summary>
/// What a Track Info window opened over the album grids should show: the
/// tracks, and - when there is a single coherent list to page through with
/// Prev/Next - which of them to focus.
/// </summary>
/// <param name="Tracks">The tracks to show. Empty means "nothing to open".</param>
/// <param name="FocusIndex">
/// The track to open on, when the window should offer Prev/Next through
/// <paramref name="Tracks"/>; null for batch mode, which edits the whole set
/// together and has no navigation.
/// </param>
public readonly record struct TrackInfoTarget(IReadOnlyList<Track> Tracks, int? FocusIndex)
{
    public static TrackInfoTarget None { get; } = new(Array.Empty<Track>(), null);

    public bool IsEmpty => Tracks.Count == 0;
}

/// <summary>
/// The precedence rules deciding what "Get Info" acts on in the Albums and
/// Recently Added grids. Pure, and deliberately outside <c>MainView</c>'s
/// code-behind: the rules are non-obvious enough to be worth testing, and
/// nothing about them is control-bound.
/// </summary>
public static class AlbumTrackInfoSelection
{
    /// <summary>
    /// Resolves the Cmd/Ctrl+I target for the album grids, in precedence order:
    /// a song selected inside the expanded album's own track list wins over any
    /// tile-level selection; exactly one such song opens in single-track mode
    /// with Prev/Next through that album; otherwise it is batch mode over the
    /// selected album tiles, falling back to whichever album is expanded.
    /// </summary>
    /// <param name="songSelection">
    /// Tracks selected within the expanded album's own row list (see
    /// <c>AlbumGridRowControl</c>). Takes priority over everything below -
    /// otherwise this always fell back to "the whole expanded album", even
    /// with one particular song selected.
    /// </param>
    /// <param name="expandedAlbumTracks">The expanded album's full track list, for Prev/Next context.</param>
    /// <param name="selectedAlbumNames">Multi-selected album tiles (Ctrl/Shift-click), if any.</param>
    /// <param name="expandedAlbumName">
    /// The currently expanded album - the common case, since a plain click
    /// expands a tile without touching the tile selection at all.
    /// </param>
    /// <param name="tracksForAlbums">Resolves album names to their tracks.</param>
    public static TrackInfoTarget Resolve(
        IReadOnlyList<Track> songSelection,
        IReadOnlyList<Track> expandedAlbumTracks,
        IReadOnlyCollection<string> selectedAlbumNames,
        string? expandedAlbumName,
        Func<IEnumerable<string>, IEnumerable<Track>> tracksForAlbums)
    {
        if (songSelection.Count == 1)
        {
            // Single-track mode, with Prev/Next through the expanded album's
            // own track list - same as AlbumGridRowControl's row context menu
            // gives for a single selected track, for the same reason: there's
            // a specific, coherent list to browse here that the "multiple
            // albums selected" case below doesn't have.
            var albumTracks = expandedAlbumTracks.ToList();
            var index = albumTracks.IndexOf(songSelection[0]);
            if (index < 0)
            {
                // The selected song isn't in the expanded album's list at all
                // (nothing expanded, or it changed underneath) - there is no
                // coherent list to page through, so show just that track.
                return new TrackInfoTarget(new[] { songSelection[0] }, 0);
            }
            return new TrackInfoTarget(albumTracks, index);
        }

        var tracks = songSelection.Count > 0
            ? songSelection
            : ResolveSelectedAlbumTracks(selectedAlbumNames, expandedAlbumName, tracksForAlbums);

        // Always batch mode, even for one album's worth of tracks - there's no
        // meaningful single-track Prev/Next context here the way there is for
        // a MusicListView row.
        return tracks.Count == 0 ? TrackInfoTarget.None : new TrackInfoTarget(tracks, null);
    }

    /// <summary>
    /// The fallback for <see cref="Resolve"/> when no specific song is selected
    /// within the expanded album: the multi-selected album tile(s) if there are
    /// any, otherwise whichever single album is currently expanded.
    /// </summary>
    public static IReadOnlyList<Track> ResolveSelectedAlbumTracks(
        IReadOnlyCollection<string> selectedAlbumNames,
        string? expandedAlbumName,
        Func<IEnumerable<string>, IEnumerable<Track>> tracksForAlbums)
    {
        var albumNames = selectedAlbumNames.Count > 0
            ? selectedAlbumNames
            : expandedAlbumName is { } expanded ? new[] { expanded } : Array.Empty<string>();

        return albumNames.Count == 0
            ? Array.Empty<Track>()
            : tracksForAlbums(albumNames).ToList();
    }
}
