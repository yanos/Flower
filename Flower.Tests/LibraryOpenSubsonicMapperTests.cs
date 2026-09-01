using System;
using System.Collections.Generic;
using System.Linq;

using Flower.Models;
using Flower.Services;

namespace Flower.Tests;

public class LibraryOpenSubsonicMapperTests
{
    private static Track RealTrack(string title, string artist, string album, int trackNumber = 1, int durationSeconds = 200) =>
        new Track
        {
            Title = title, Artists = artist, Album = album, TrackNumber = (uint)trackNumber,
            Duration = TimeSpan.FromSeconds(durationSeconds), Year = "1999", Genre = "Rock",
            Path = $"/music/{title}.mp3",
        };

    [Fact]
    public void BuildAlbumList_excludes_placeholder_tracks()
    {
        var real = RealTrack("A", "Artist", "Album");
        var placeholder = new Track { Title = "B", Artists = "Artist", Album = "Other Album", Path = null, OriginDeviceFingerprint = "peer-1" };

        var albums = LibraryOpenSubsonicMapper.BuildAlbumList(Snapshot(real, placeholder));

        var album = Assert.Single(albums);
        Assert.Equal("Album", album.Name);
    }

    [Fact]
    public void BuildAlbumList_groups_by_album_and_artist_and_counts_songs()
    {
        var tracks = new List<Track>
        {
            RealTrack("Track1", "Beatles", "Abbey Road", trackNumber: 1),
            RealTrack("Track2", "Beatles", "Abbey Road", trackNumber: 2),
        };

        var album = Assert.Single(LibraryOpenSubsonicMapper.BuildAlbumList(LibrarySnapshot.Build(tracks)));

        Assert.Equal("Abbey Road", album.Name);
        Assert.Equal("Beatles", album.Artist);
        Assert.Equal(2, album.SongCount);
        Assert.Equal(400, album.Duration);
    }

    [Fact]
    public void BuildAlbumList_does_not_collide_same_named_albums_by_different_artists()
    {
        var tracks = new List<Track>
        {
            RealTrack("Track1", "Artist A", "Greatest Hits"),
            RealTrack("Track2", "Artist B", "Greatest Hits"),
        };

        var albums = LibraryOpenSubsonicMapper.BuildAlbumList(LibrarySnapshot.Build(tracks));

        Assert.Equal(2, albums.Count);
        Assert.Equal(2, albums.Select(a => a.Id).Distinct().Count());
    }

    [Fact]
    public void BuildAlbumList_does_not_fragment_a_various_artists_compilation_by_per_track_artist()
    {
        var tracks = new List<Track>
        {
            RealTrack("Track1", "Artist A", "Compilation"),
            RealTrack("Track2", "Artist B", "Compilation"),
            RealTrack("Track3", "Artist C", "Compilation"),
        };
        foreach (var t in tracks)
            t.AlbumArtists = "Various Artists";

        var album = Assert.Single(LibraryOpenSubsonicMapper.BuildAlbumList(LibrarySnapshot.Build(tracks)));

        Assert.Equal("Various Artists", album.Artist);
        Assert.Equal(3, album.SongCount);
    }

    [Fact]
    public void BuildAlbumList_does_not_fragment_a_compilation_flagged_track_with_no_AlbumArtists_tag()
    {
        var tracks = new List<Track>
        {
            RealTrack("Track1", "Artist A", "Compilation"),
            RealTrack("Track2", "Artist B", "Compilation"),
        };
        foreach (var t in tracks)
            t.IsCompilation = true;

        var album = Assert.Single(LibraryOpenSubsonicMapper.BuildAlbumList(LibrarySnapshot.Build(tracks)));

        Assert.Equal("Various Artists", album.Artist);
        Assert.Equal(2, album.SongCount);
    }

    [Fact]
    public void BuildAllSongs_returns_every_real_track_across_every_album_in_one_flat_list()
    {
        var tracks = new List<Track>
        {
            RealTrack("Track1", "Beatles", "Abbey Road"),
            RealTrack("Track2", "Beatles", "Abbey Road"),
            RealTrack("Track3", "Miles Davis", "Kind of Blue"),
        };
        var placeholder = new Track { Title = "Not Downloaded", Artists = "X", Album = "Y", Path = null, OriginDeviceFingerprint = "peer-1" };

        var songs = LibraryOpenSubsonicMapper.BuildAllSongs(LibrarySnapshot.Build(tracks.Append(placeholder).ToList()), "self-1");

        Assert.Equal(3, songs.Count);
        Assert.DoesNotContain(songs, s => s.Title == "Not Downloaded");
    }

    [Fact]
    public void FindAlbum_returns_null_for_an_unknown_id()
    {
        Assert.Null(LibraryOpenSubsonicMapper.FindAlbum(Snapshot(), "al:nope|nope", "self-1"));
    }

    [Fact]
    public void FindAlbum_returns_the_full_song_list_for_a_known_album()
    {
        var tracks = new List<Track>
        {
            RealTrack("Come Together", "Beatles", "Abbey Road", trackNumber: 1, durationSeconds: 259),
            RealTrack("Something", "Beatles", "Abbey Road", trackNumber: 2, durationSeconds: 183),
        };
        var albumId = SubsonicIdentity.AlbumId("Beatles", "Abbey Road");

        var album = LibraryOpenSubsonicMapper.FindAlbum(LibrarySnapshot.Build(tracks), albumId, "self-1");

        Assert.NotNull(album);
        Assert.Equal(2, album!.Song?.Count);
        Assert.Equal("Come Together", album.Song![0].Title);
        Assert.Equal(259, album.Song[0].Duration);
        Assert.Equal(albumId, album.Song[0].AlbumId);
    }

    // Confirmed on a real device: a peer that fetches this Duration field
    // rebuilds a placeholder Track from it (TimeSpan.FromSeconds(Duration))
    // and later recomputes its own Track.SyncKey from that placeholder to
    // request a stream/download - which also rounds via Math.Round. If this
    // mapper truncated instead of rounding, a track whose real duration's
    // fractional part is >= .5s would report a Duration one second lower than
    // SyncKey's own rounding, so the peer's later request would carry a
    // SyncKey this device could never match against its own track (a 404,
    // "no matching track for that id", indistinguishable from the peer
    // simply being unreachable without the logging HandleStreamAsync now has).
    [Fact]
    public void ToChild_Duration_rounds_to_match_SyncKey_rather_than_truncating()
    {
        var track = new Track
        {
            Title = "Mata Zyklek", Artists = "Angine de Poitrine", Album = "Vol.II",
            Duration = TimeSpan.FromSeconds(369.888), Path = "/music/Mata Zyklek.mp3",
        };
        Assert.EndsWith("|370", track.SyncKey); // Sanity check on the premise itself.

        var albumId = SubsonicIdentity.AlbumId("Angine de Poitrine", "Vol.II");
        var album = LibraryOpenSubsonicMapper.FindAlbum(Snapshot(track), albumId, "self-1");

        Assert.Equal(370, album!.Song![0].Duration);
    }

    // The serving half of what LibrarySyncMapperTests reads back. Both sides of
    // the wire have to agree or the fields simply never arrive - which is what
    // happened to the technical fields for a whole release.
    [Fact]
    public void ToChild_sends_the_sort_tags_the_playback_options_and_the_encoder_profile()
    {
        var track = new Track
        {
            Title = "Come Together", Artists = "The Beatles", Album = "Abbey Road",
            Duration = TimeSpan.FromSeconds(259), Path = "/music/come together.mp3",
            TitleSort = "Come Together", ArtistsSort = "Beatles, The",
            AlbumSort = "Abbey Road", ComposersSort = "Lennon, John",
            RememberPlaybackPosition = true, ResumePosition = TimeSpan.FromSeconds(754),
            IgnoreWhenShuffling = true, VolumeAdjustment = -20,
            EncoderProfile = "LAME 3.100, VBR (V0)",
        };

        var albumId = SubsonicIdentity.AlbumId("The Beatles", "Abbey Road");
        var song = LibraryOpenSubsonicMapper.FindAlbum(Snapshot(track), albumId, "self-1")!.Song!.Single();

        Assert.Equal("Beatles, The", song.SortArtist);
        Assert.Equal("Come Together", song.SortTitle);
        Assert.Equal("Abbey Road", song.SortAlbum);
        Assert.Equal("Lennon, John", song.SortComposer);
        Assert.True(song.RememberPlaybackPosition);
        Assert.Equal(754, song.ResumePositionSeconds);
        Assert.True(song.IgnoreWhenShuffling);
        Assert.Equal(-20, song.VolumeAdjustment);
        Assert.Equal("LAME 3.100, VBR (V0)", song.EncoderProfile);
    }

    [Fact]
    public void ToChild_Suffix_is_the_local_file_extension_without_a_leading_dot()
    {
        var track = RealTrack("Come Together", "Beatles", "Abbey Road");

        var album = LibraryOpenSubsonicMapper.FindAlbum(Snapshot(track), SubsonicIdentity.AlbumId("Beatles", "Abbey Road"), "self-1");

        Assert.Equal("mp3", album!.Song!.Single().Suffix);
    }

    [Fact]
    public void ToChild_song_id_is_the_tracks_own_stable_Id_not_its_tag_derived_SyncKey()
    {
        var track = RealTrack("Come Together", "Beatles", "Abbey Road", durationSeconds: 259);

        var album = LibraryOpenSubsonicMapper.FindAlbum(Snapshot(track), SubsonicIdentity.AlbumId("Beatles", "Abbey Road"), "self-1");

        Assert.Equal(track.Id.ToString("N"), album!.Song!.Single().Id);
        Assert.NotEqual(track.SyncKey, album.Song!.Single().Id);
    }

    // The point of the change: SyncKey is derived from Title/Artist/Album and a
    // rounded duration, so serving it as the song id meant a tag edit here
    // invalidated every id a peer was still holding - its next stream or
    // download request 404'd, indistinguishable from the peer being offline.
    [Fact]
    public void ToChild_song_id_survives_a_tag_edit_on_the_serving_device()
    {
        var track = RealTrack("Come Together", "Beatles", "Abbey Road", durationSeconds: 259);
        var before = LibraryOpenSubsonicMapper.FindAlbum(Snapshot(track), LibraryOpenSubsonicMapper.AlbumIdFor(track), "self-1")!.Song!.Single().Id;

        track.Title = "Come Together (Remastered)";
        var after = LibraryOpenSubsonicMapper.FindAlbum(Snapshot(track), LibraryOpenSubsonicMapper.AlbumIdFor(track), "self-1")!.Song!.Single().Id;

        Assert.Equal(before, after);
        Assert.NotEqual(track.SyncKey, after); // Sanity check on the premise: SyncKey did move.
    }

    [Fact]
    public void ToChild_PlayCounts_includes_our_own_tally_under_our_own_fingerprint()
    {
        var track = RealTrack("Come Together", "Beatles", "Abbey Road");
        track.PlayCount = 3;
        track.ImportedPlayCount = 4;

        var album = LibraryOpenSubsonicMapper.FindAlbum(Snapshot(track), SubsonicIdentity.AlbumId("Beatles", "Abbey Road"), "self-1");

        Assert.Equal(7, album!.Song!.Single().PlayCounts!["self-1"]);
    }

    [Fact]
    public void ToChild_PlayCounts_carries_forward_every_other_device_this_track_already_knows_about()
    {
        var track = RealTrack("Come Together", "Beatles", "Abbey Road");
        track.RemotePlayCounts = new Dictionary<string, int> { ["peer-2"] = 12 };

        var album = LibraryOpenSubsonicMapper.FindAlbum(Snapshot(track), SubsonicIdentity.AlbumId("Beatles", "Abbey Road"), "self-1");

        Assert.Equal(12, album!.Song!.Single().PlayCounts!["peer-2"]);
    }

    // ── One identity scheme, client and server (Tier 2.1) ────────────────────

    [Fact]
    public void AlbumIdFor_is_the_album_artist_not_the_track_artist()
    {
        // A compilation: one album artist, many track artists. Deriving the
        // id from Artists fragments the album into one id per track, and the
        // grouped id nothing ever hands out - which is exactly how remote
        // cover art for every compilation used to 404 (AlbumArtLoader and
        // the cover-art handler both asked with the track artist while the
        // manifest was built with the album artist).
        var first = new Track
        {
            Title = "One", Artists = "Artist A", AlbumArtists = "Various Artists",
            Album = "Compilation", Path = "/music/one.mp3",
        };
        var second = new Track
        {
            Title = "Two", Artists = "Artist B", AlbumArtists = "Various Artists",
            Album = "Compilation", Path = "/music/two.mp3",
        };

        Assert.Equal(LibraryOpenSubsonicMapper.AlbumIdFor(first), LibraryOpenSubsonicMapper.AlbumIdFor(second));
        Assert.Equal(SubsonicIdentity.AlbumId("Various Artists", "Compilation"), LibraryOpenSubsonicMapper.AlbumIdFor(first));

        // And the id the album listing actually publishes is that same one.
        var album = Assert.Single(LibraryOpenSubsonicMapper.BuildAlbumList(Snapshot(first, second)));
        Assert.Equal(LibraryOpenSubsonicMapper.AlbumIdFor(first), album.Id);
    }

    [Fact]
    public void A_songs_ArtistId_is_the_albums_artist_so_it_points_at_an_artist_the_listing_mentions()
    {
        var track = new Track
        {
            Title = "One", Artists = "Artist A", AlbumArtists = "Various Artists",
            Album = "Compilation", Path = "/music/one.mp3",
        };

        var album = LibraryOpenSubsonicMapper.FindAlbum(Snapshot(track), LibraryOpenSubsonicMapper.AlbumIdFor(track), "self-1");

        Assert.Equal(SubsonicIdentity.ArtistId("Various Artists"), album!.Song!.Single().ArtistId);
        Assert.Equal(album.ArtistId, album.Song!.Single().ArtistId);
    }

    [Theory]
    [InlineData("Beatles", "Abbey Road")]
    [InlineData("  BEATLES  ", "abbey road")] // Normalized: trimmed and lowercased.
    [InlineData("A|B", "C")] // The old plain-text form embedded this separator into the id itself.
    public void Ids_are_opaque_and_normalized(string artist, string album)
    {
        var id = SubsonicIdentity.AlbumId(artist, album);

        Assert.Equal(SubsonicIdentity.AlbumId(artist.Trim().ToUpperInvariant(), album.ToUpperInvariant()), id);
        Assert.StartsWith("al-", id);
        Assert.DoesNotContain(artist.Trim().ToLowerInvariant(), id);
        Assert.NotEqual(SubsonicIdentity.AlbumId(album, artist), id); // Argument order is meaningful.
    }

    // The mapper reads through the library's own grouped snapshot now, so a
    // test that wants to map a handful of tracks builds one over them.
    private static LibrarySnapshot Snapshot(params Track[] tracks) => LibrarySnapshot.Build(tracks);
}
