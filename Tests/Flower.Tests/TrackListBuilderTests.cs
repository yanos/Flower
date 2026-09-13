using System;
using System.Collections.Generic;
using Flower.Models;
using Flower.Services;

namespace Flower.Tests;

public class TrackListBuilderTests
{
    private static Track T(
        string title, string artist = "X", string album = "Y", uint trackNumber = 1,
        uint discNumber = 0, string? genre = null, string? path = null, string? year = null,
        DateTimeOffset? dateAdded = null, DateTimeOffset? lastPlayedAt = null,
        int playCount = 0, int importedPlayCount = 0)
        => new Track
        {
            Title             = title,
            Artists           = artist,
            Album             = album,
            TrackNumber       = trackNumber,
            DiscNumber        = discNumber,
            Genre             = genre,
            Year              = year,
            Path              = path ?? $"/music/{artist}/{album}/{title}.mp3",
            DateAdded         = dateAdded ?? DateTimeOffset.UtcNow,
            LastPlayedAt      = lastPlayedAt,
            PlayCount         = playCount,
            ImportedPlayCount = importedPlayCount,
        };

    [Fact]
    public void Filter_matches_title_artist_album_and_genre_case_insensitively()
    {
        var tracks = new List<Track>
        {
            T("Sunrise",   artist: "Aurora", album: "Dawn", genre: "Ambient"),
            T("Nightfall", artist: "Nova",   album: "Dusk", genre: "Electronic"),
        };

        var rows = TrackListBuilder.Build(tracks, "aurora", "Title", true);
        Assert.Single(rows);
        Assert.Equal("Sunrise", rows[0].Track.Title);

        rows = TrackListBuilder.Build(tracks, "ELECTRONIC", "Title", true);
        Assert.Single(rows);
        Assert.Equal("Nightfall", rows[0].Track.Title);
    }

    [Fact]
    public void Filter_with_blank_text_returns_all_tracks()
    {
        var tracks = new List<Track> { T("A"), T("B") };
        var rows = TrackListBuilder.Build(tracks, "   ", "Title", true);
        Assert.Equal(2, rows.Count);
    }

    [Fact]
    public void Sort_by_title_ascending_and_descending()
    {
        var tracks = new List<Track> { T("Bravo"), T("Alpha"), T("Charlie") };

        var asc = TrackListBuilder.Build(tracks, null, "Title", true);
        Assert.Equal(new[] { "Alpha", "Bravo", "Charlie" }, asc.ConvertAll(r => r.Track.Title));

        var desc = TrackListBuilder.Build(tracks, null, "Title", false);
        Assert.Equal(new[] { "Charlie", "Bravo", "Alpha" }, desc.ConvertAll(r => r.Track.Title));
    }

    [Fact]
    public void Sort_by_title_ignores_non_alphanumeric_characters()
    {
        var tracks = new List<Track>
        {
            T("Bravo!"),
            T("(Alpha)"),
            T("Char-lie"),
        };

        var rows = TrackListBuilder.Build(tracks, null, "Title", true);

        Assert.Equal(new[] { "(Alpha)", "Bravo!", "Char-lie" }, rows.ConvertAll(r => r.Track.Title));
    }

    [Fact]
    public void Sort_by_tracknumber_orders_by_album_then_disc_then_track_number()
    {
        var tracks = new List<Track>
        {
            T("B-Disc2-Track1", album: "Album B", discNumber: 2, trackNumber: 1),
            T("A-Track2",       album: "Album A", discNumber: 1, trackNumber: 2),
            T("A-Track1",       album: "Album A", discNumber: 1, trackNumber: 1),
            T("B-Disc1-Track1", album: "Album B", discNumber: 1, trackNumber: 1),
        };

        var rows = TrackListBuilder.Build(tracks, null, "TrackNumber", true);

        Assert.Equal(
            new[] { "A-Track1", "A-Track2", "B-Disc1-Track1", "B-Disc2-Track1" },
            rows.ConvertAll(r => r.Track.Title));
    }

    [Fact]
    public void Sort_by_artist_orders_each_artists_albums_alphabetically_by_default()
    {
        var tracks = new List<Track>
        {
            T("Newer", artist: "Nova", album: "2020 Album", year: "2020"),
            T("Older", artist: "Nova", album: "2010 Album", year: "2010"),
        };

        var rows = TrackListBuilder.Build(tracks, null, "Artist", true);

        // "2010 Album" sorts before "2020 Album" alphabetically, ignoring year.
        Assert.Equal(new[] { "Older", "Newer" }, rows.ConvertAll(r => r.Track.Title));
    }

    [Fact]
    public void Sort_by_artist_with_albums_by_year_breaks_ties_on_same_year_alphabetically()
    {
        var tracks = new List<Track>
        {
            T("Zebra",  artist: "Nova", album: "Zebra Album",  year: "2020"),
            T("Aurora", artist: "Nova", album: "Aurora Album", year: "2020"),
        };

        var rows = TrackListBuilder.Build(tracks, null, "Artist", true, sortArtistAlbumsByYear: true);

        Assert.Equal(new[] { "Aurora", "Zebra" }, rows.ConvertAll(r => r.Track.Title));
    }

    [Fact]
    public void Sort_by_artist_with_albums_by_year_orders_each_artists_albums_chronologically()
    {
        var tracks = new List<Track>
        {
            T("B-Newer", artist: "Beta",  album: "2020 Album", year: "2020"),
            T("A-Newer", artist: "Alpha", album: "2020 Album", year: "2020"),
            T("A-Older", artist: "Alpha", album: "2010 Album", year: "2010"),
            T("B-Older", artist: "Beta",  album: "2010 Album", year: "2010"),
        };

        var rows = TrackListBuilder.Build(tracks, null, "Artist", true, sortArtistAlbumsByYear: true);

        Assert.Equal(
            new[] { "A-Older", "A-Newer", "B-Older", "B-Newer" },
            rows.ConvertAll(r => r.Track.Title));
    }

    [Fact]
    public void Sort_by_album_groups_consecutive_same_album_tracks()
    {
        var tracks = new List<Track>
        {
            T("A1", album: "Alpha Album", trackNumber: 1),
            T("B1", album: "Beta Album",  trackNumber: 1),
            T("A2", album: "Alpha Album", trackNumber: 2),
        };

        var rows = TrackListBuilder.Build(tracks, null, "Album", true);

        Assert.Equal(new[] { "A1", "A2", "B1" }, rows.ConvertAll(r => r.Track.Title));

        Assert.True(rows[0].IsFirstInAlbumGroup);
        Assert.Equal(2, rows[0].AlbumGroupSize);
        Assert.False(rows[1].IsFirstInAlbumGroup);
        Assert.Equal(2, rows[1].AlbumGroupSize);
        Assert.True(rows[2].IsFirstInAlbumGroup);
        Assert.Equal(1, rows[2].AlbumGroupSize);
    }

    // Descending used to be implemented as Enumerable.Reverse over the fully
    // sorted sequence, which reverses ties as well as the primary key - so
    // Album-descending also listed every album's own tracks back to front.
    [Fact]
    public void Sort_by_album_descending_keeps_track_order_within_each_album()
    {
        var tracks = new List<Track>
        {
            T("A1", album: "Alpha Album", trackNumber: 1),
            T("A2", album: "Alpha Album", trackNumber: 2),
            T("B1", album: "Beta Album",  trackNumber: 1),
            T("B2", album: "Beta Album",  trackNumber: 2),
        };

        var rows = TrackListBuilder.Build(tracks, null, "Album", false);

        Assert.Equal(new[] { "B1", "B2", "A1", "A2" }, rows.ConvertAll(r => r.Track.Title));
    }

    [Fact]
    public void Sort_by_artist_descending_keeps_album_and_track_order_within_each_artist()
    {
        var tracks = new List<Track>
        {
            T("A-1", artist: "Alpha", album: "First",  trackNumber: 1),
            T("A-2", artist: "Alpha", album: "Second", trackNumber: 1),
            T("B-1", artist: "Beta",  album: "First",  trackNumber: 1),
            T("B-2", artist: "Beta",  album: "Second", trackNumber: 1),
        };

        var rows = TrackListBuilder.Build(tracks, null, "Artist", false);

        Assert.Equal(new[] { "B-1", "B-2", "A-1", "A-2" }, rows.ConvertAll(r => r.Track.Title));
    }

    [Fact]
    public void Sort_by_dateadded_ascending_and_descending()
    {
        var older = T("Older", dateAdded: new DateTimeOffset(2020, 1, 1, 0, 0, 0, TimeSpan.Zero));
        var newer = T("Newer", dateAdded: new DateTimeOffset(2023, 1, 1, 0, 0, 0, TimeSpan.Zero));
        var tracks = new List<Track> { newer, older };

        var asc = TrackListBuilder.Build(tracks, null, "DateAdded", true);
        Assert.Equal(new[] { "Older", "Newer" }, asc.ConvertAll(r => r.Track.Title));

        var desc = TrackListBuilder.Build(tracks, null, "DateAdded", false);
        Assert.Equal(new[] { "Newer", "Older" }, desc.ConvertAll(r => r.Track.Title));
    }

    [Fact]
    public void Sort_by_lastplayed_ascending_and_descending()
    {
        var playedOlder = T("PlayedOlder", lastPlayedAt: new DateTimeOffset(2020, 1, 1, 0, 0, 0, TimeSpan.Zero));
        var playedNewer = T("PlayedNewer", lastPlayedAt: new DateTimeOffset(2023, 1, 1, 0, 0, 0, TimeSpan.Zero));
        var tracks = new List<Track> { playedNewer, playedOlder };

        var asc = TrackListBuilder.Build(tracks, null, "LastPlayed", true);
        Assert.Equal(new[] { "PlayedOlder", "PlayedNewer" }, asc.ConvertAll(r => r.Track.Title));

        var desc = TrackListBuilder.Build(tracks, null, "LastPlayed", false);
        Assert.Equal(new[] { "PlayedNewer", "PlayedOlder" }, desc.ConvertAll(r => r.Track.Title));
    }

    [Fact]
    public void Sort_by_playcount_ascending_and_descending()
    {
        var tracks = new List<Track>
        {
            T("Popular",  album: "A", playCount: 42),
            T("Unplayed", album: "B", playCount: 0),
            T("Some",     album: "C", playCount: 7),
        };

        var asc = TrackListBuilder.Build(tracks, null, "PlayCount", true);
        Assert.Equal(new[] { "Unplayed", "Some", "Popular" }, asc.ConvertAll(r => r.Track.Title));

        var desc = TrackListBuilder.Build(tracks, null, "PlayCount", false);
        Assert.Equal(new[] { "Popular", "Some", "Unplayed" }, desc.ConvertAll(r => r.Track.Title));
    }

    [Fact]
    public void Sort_by_playcount_combines_Flower_and_imported_counts()
    {
        // "Imported" has more plays overall once its iTunes-imported count (see
        // Track.ImportedPlayCount/ITunesPlayCountImporter) is added to Flower's
        // own - sorting must reflect that combined total, matching what
        // TrackRowViewModel.PlayCountDisplay actually shows.
        var tracks = new List<Track>
        {
            T("FlowerOnly", album: "A", playCount: 10, importedPlayCount: 0),
            T("Imported",   album: "B", playCount: 2,  importedPlayCount: 20),
        };

        var asc = TrackListBuilder.Build(tracks, null, "PlayCount", true);
        Assert.Equal(new[] { "FlowerOnly", "Imported" }, asc.ConvertAll(r => r.Track.Title));
    }

    [Fact]
    public void Sort_by_column_other_than_album_or_tracknumber_still_groups_contiguous_same_album_tracks()
    {
        var tracks = new List<Track>
        {
            T("A1", album: "Same Album", trackNumber: 1),
            T("A2", album: "Same Album", trackNumber: 2),
        };

        var rows = TrackListBuilder.Build(tracks, null, "Title", true);

        Assert.True(rows[0].IsFirstInAlbumGroup);
        Assert.Equal(2, rows[0].AlbumGroupSize);
        Assert.False(rows[1].IsFirstInAlbumGroup);
        Assert.Equal(2, rows[1].AlbumGroupSize);
    }

    [Fact]
    public void PlaylistOrder_preserves_input_order_and_still_groups_contiguous_same_album_tracks()
    {
        var tracks = new List<Track>
        {
            T("Third",  album: "Album", trackNumber: 3),
            T("First",  album: "Album", trackNumber: 1),
            T("Second", album: "Album", trackNumber: 2),
        };

        var rows = TrackListBuilder.Build(tracks, null, "PlaylistOrder", true);

        Assert.Equal(new[] { "Third", "First", "Second" }, rows.ConvertAll(r => r.Track.Title));
        Assert.True(rows[0].IsFirstInAlbumGroup);
        Assert.Equal(3, rows[0].AlbumGroupSize);
        Assert.False(rows[1].IsFirstInAlbumGroup);
        Assert.False(rows[2].IsFirstInAlbumGroup);
    }

    [Fact]
    public void IsCurrentlyPlaying_is_set_by_matching_track_path()
    {
        var playing = T("Now Playing", path: "/music/playing.mp3");
        var tracks  = new List<Track> { playing, T("Other", path: "/music/other.mp3") };

        var rows = TrackListBuilder.Build(tracks, null, "Title", true, playing);

        Assert.True(rows.Find(r => r.Track.Path == "/music/playing.mp3")!.IsCurrentlyPlaying);
        Assert.False(rows.Find(r => r.Track.Path == "/music/other.mp3")!.IsCurrentlyPlaying);
    }

    // Regression: tracks[k].Path == currentlyPlaying?.Path alone is true for
    // null == null whenever nothing is playing - every not-yet-downloaded
    // placeholder (Path == null too) then wrongly matched "currently
    // playing" and got the bold/accent-color row styling meant for an
    // actual playing track.
    [Fact]
    public void IsCurrentlyPlaying_is_false_for_placeholders_when_nothing_is_playing()
    {
        var tracks = new List<Track>
        {
            new Track { Title = "Placeholder One", Path = null, OriginDeviceFingerprint = "peer-1" },
            new Track { Title = "Placeholder Two", Path = null, OriginDeviceFingerprint = "peer-1" },
        };

        var rows = TrackListBuilder.Build(tracks, null, "Title", true, currentlyPlayingTrack: null);

        Assert.All(rows, r => Assert.False(r.IsCurrentlyPlaying));
    }

    // ── Tier 1.5: the allocation-free SortKey rewrite ────────────────────────

    [Fact]
    public void Title_sort_still_ignores_punctuation_symbols_and_spacing()
    {
        // SortKey strips everything but letters and digits before comparing.
        // The hand-rolled version that replaced the LINQ one-liner has to keep
        // that behaviour exactly, including the "nothing was stripped, return
        // the input untouched" fast path and the all-stripped empty case.
        var tracks = new List<Track>
        {
            new Track { Title = "!!!" },          // strips to "" - sorts first
            new Track { Title = "b" },            // nothing to strip
            new Track { Title = "\"A Song\"" },   // strips to "ASong"
        };

        var titles = TrackListBuilder.Build(tracks, "", "Title", true)
            .Select(r => r.Track.Title)
            .ToList();

        Assert.Equal(["!!!", "\"A Song\"", "b"], titles);
    }

    // ── "Sort as" overrides (Track Info's Options tab) ──────────────────────
    //
    // The whole point of the sort tags: the list still *shows* "The Beatles"
    // and files it under B.

    [Fact]
    public void Sorting_by_artist_uses_the_sort_tag_where_one_is_set()
    {
        var beatles = T("Something", artist: "The Beatles", album: "Abbey Road");
        beatles.ArtistsSort = "Beatles, The";
        var cash = T("Hurt", artist: "Johnny Cash", album: "American IV");
        var abba = T("SOS", artist: "ABBA", album: "Ring Ring");

        var rows = TrackListBuilder.Plan([beatles, cash, abba], null, "Artist", true);

        // ABBA, Beatles (via its override), Johnny - not ABBA, Johnny, The.
        Assert.Equal(["SOS", "Something", "Hurt"], rows.Select(r => r.Track.Title));
    }

    [Fact]
    public void Sorting_by_title_uses_the_sort_tag_where_one_is_set()
    {
        var a = T("The Wall", album: "A");
        a.TitleSort = "Wall, The";
        var b = T("Umbrella", album: "B");

        var rows = TrackListBuilder.Plan([a, b], null, "Title", true);

        Assert.Equal(["Umbrella", "The Wall"], rows.Select(r => r.Track.Title));
    }

    // An override left as an empty string - which is what a cleared box and
    // some third-party taggers both produce - must not sort the track above
    // everything else. See Track.SortAs.
    [Fact]
    public void A_blank_sort_tag_falls_back_to_the_displayed_value()
    {
        var a = T("Apple", album: "A");
        var z = T("Zebra", album: "Z");
        z.TitleSort = "   ";

        var rows = TrackListBuilder.Plan([z, a], null, "Title", true);

        Assert.Equal(["Apple", "Zebra"], rows.Select(r => r.Track.Title));
    }
}
