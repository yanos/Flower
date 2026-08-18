using System;
using System.Collections.Generic;

using Flower.Services;

namespace Flower.Tests;

public class LibrarySyncMapperTests
{
    [Fact]
    public void ToPlaceholderTrack_maps_metadata_and_leaves_Path_null()
    {
        var song = new Child(
            Id: "some-id", Title: "Come Together", Album: "Abbey Road", Artist: "Beatles",
            AlbumId: "al:1", ArtistId: "ar:1", Track: 1, Year: 1969, Genre: "Rock",
            Size: null, ContentType: null, Suffix: "mp3", Duration: 259, BitRate: null, CoverArt: null);

        var track = LibrarySyncMapper.ToPlaceholderTrack(song, "peer-1", "self-1");

        Assert.Equal("Come Together", track.Title);
        Assert.Equal("Beatles", track.Artists);
        Assert.Equal("Abbey Road", track.Album);
        Assert.Equal(TimeSpan.FromSeconds(259), track.Duration);
        Assert.Equal("Rock", track.Genre);
        Assert.Equal("1969", track.Year);
        Assert.Equal(1u, track.TrackNumber);
        Assert.Null(track.Path);
        Assert.Equal("peer-1", track.OriginDeviceFingerprint);
        Assert.Equal("mp3", track.OriginFileExtension);
        Assert.Equal("some-id", track.OriginTrackId);
    }

    // The peer's id is stored verbatim rather than re-derived, so it keeps
    // working across a tag edit on the serving side and works at all against a
    // standalone Flower.Server, whose ids are database row ids that no amount
    // of local recomputation would ever produce. Note this track's own Id is a
    // fresh local one - the two identities are deliberately separate.
    [Fact]
    public void ToPlaceholderTrack_keeps_the_peers_own_id_verbatim_and_does_not_adopt_it_as_its_own()
    {
        var song = new Child(
            Id: "row-42-on-the-server", Title: "Come Together", Album: "Abbey Road", Artist: "Beatles",
            AlbumId: null, ArtistId: null, Track: 1, Year: 1969, Genre: "Rock",
            Size: null, ContentType: null, Suffix: null, Duration: 259, BitRate: null, CoverArt: null);

        var track = LibrarySyncMapper.ToPlaceholderTrack(song, "peer-1", "self-1");

        Assert.Equal("row-42-on-the-server", track.OriginTrackId);
        Assert.NotEqual("row-42-on-the-server", track.Id.ToString("N"));
    }

    [Fact]
    public void ToPlaceholderTrack_carries_the_origin_peers_album_art_hash()
    {
        var song = new Child(
            Id: "some-id", Title: "Come Together", Album: "Abbey Road", Artist: "Beatles",
            AlbumId: "al:1", ArtistId: "ar:1", Track: 1, Year: 1969, Genre: "Rock",
            Size: null, ContentType: null, Suffix: "mp3", Duration: 259, BitRate: null, CoverArt: "abc123");

        var track = LibrarySyncMapper.ToPlaceholderTrack(song, "peer-1", "self-1");

        Assert.Equal("abc123", track.OriginAlbumArtHash);
    }

    [Fact]
    public void ToPlaceholderTrack_defaults_TrackNumber_to_zero_when_absent()
    {
        var song = new Child(
            Id: "id", Title: "Untitled", Album: null, Artist: null,
            AlbumId: null, ArtistId: null, Track: null, Year: null, Genre: null,
            Size: null, ContentType: null, Suffix: null, Duration: 100, BitRate: null, CoverArt: null);

        var track = LibrarySyncMapper.ToPlaceholderTrack(song, "peer-1", "self-1");

        Assert.Equal(0u, track.TrackNumber);
    }

    [Fact]
    public void ToPlaceholderTrack_SyncKey_matches_what_the_server_side_mapper_would_compute_for_the_same_track()
    {
        var song = new Child(
            Id: "id", Title: "Come Together", Album: "Abbey Road", Artist: "Beatles",
            AlbumId: null, ArtistId: null, Track: 1, Year: 1969, Genre: "Rock",
            Size: null, ContentType: null, Suffix: null, Duration: 259, BitRate: null, CoverArt: null);

        var placeholder = LibrarySyncMapper.ToPlaceholderTrack(song, "peer-1", "self-1");

        // The wire "id" (song.Id - the peer's own Track.Id, see
        // LibraryOpenSubsonicMapper.ToChild) is what the track is *addressed*
        // by, and is kept as OriginTrackId - but it is deliberately not the
        // cross-device *matching* identity (see SYNC-PLAN.md Phase 3), since
        // two devices that each imported the same song separately have no
        // reason to share one. That matching is SyncKey, recomputed here from
        // title/artist/album/duration, and it must land on the exact same value
        // the server-side track's own SyncKey would.
        Assert.Equal(Flower.Models.Track.BuildSyncKey("Come Together", "Beatles", "Abbey Road", 259), placeholder.SyncKey);
    }

    [Fact]
    public void ToPlaceholderTrack_carries_the_incoming_play_counts_into_RemotePlayCounts()
    {
        var song = new Child(
            Id: "id", Title: "Come Together", Album: "Abbey Road", Artist: "Beatles",
            AlbumId: null, ArtistId: null, Track: 1, Year: 1969, Genre: "Rock",
            Size: null, ContentType: null, Suffix: null, Duration: 259, BitRate: null, CoverArt: null,
            PlayCounts: new Dictionary<string, int> { ["peer-1"] = 4, ["peer-2"] = 9 });

        var track = LibrarySyncMapper.ToPlaceholderTrack(song, "peer-1", "self-1");

        Assert.Equal(4, track.RemotePlayCounts["peer-1"]);
        Assert.Equal(9, track.RemotePlayCounts["peer-2"]);
    }

    [Fact]
    public void ToPlaceholderTrack_excludes_our_own_fingerprint_from_the_incoming_play_counts()
    {
        // The peer answering this request may have previously learned our own
        // play count via an earlier sync and be echoing it straight back - our
        // own count must stay authoritative locally (Track.PlayCount), never
        // overwritten by something arriving over the wire.
        var song = new Child(
            Id: "id", Title: "Come Together", Album: "Abbey Road", Artist: "Beatles",
            AlbumId: null, ArtistId: null, Track: 1, Year: 1969, Genre: "Rock",
            Size: null, ContentType: null, Suffix: null, Duration: 259, BitRate: null, CoverArt: null,
            PlayCounts: new Dictionary<string, int> { ["self-1"] = 999, ["peer-2"] = 9 });

        var track = LibrarySyncMapper.ToPlaceholderTrack(song, "peer-1", "self-1");

        Assert.False(track.RemotePlayCounts.ContainsKey("self-1"));
        Assert.Equal(9, track.RemotePlayCounts["peer-2"]);
    }
}
