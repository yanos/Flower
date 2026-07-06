using System;

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

        var track = LibrarySyncMapper.ToPlaceholderTrack(song, "peer-1");

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
    }

    [Fact]
    public void ToPlaceholderTrack_defaults_TrackNumber_to_zero_when_absent()
    {
        var song = new Child(
            Id: "id", Title: "Untitled", Album: null, Artist: null,
            AlbumId: null, ArtistId: null, Track: null, Year: null, Genre: null,
            Size: null, ContentType: null, Suffix: null, Duration: 100, BitRate: null, CoverArt: null);

        var track = LibrarySyncMapper.ToPlaceholderTrack(song, "peer-1");

        Assert.Equal(0u, track.TrackNumber);
    }

    [Fact]
    public void ToPlaceholderTrack_SyncKey_matches_what_the_server_side_mapper_would_compute_for_the_same_track()
    {
        var song = new Child(
            Id: "id", Title: "Come Together", Album: "Abbey Road", Artist: "Beatles",
            AlbumId: null, ArtistId: null, Track: 1, Year: 1969, Genre: "Rock",
            Size: null, ContentType: null, Suffix: null, Duration: 259, BitRate: null, CoverArt: null);

        var placeholder = LibrarySyncMapper.ToPlaceholderTrack(song, "peer-1");

        // The wire "id" (song.Id, e.g. a peer's own SyncKey - see
        // LibraryOpenSubsonicMapper.ToChild) is deliberately not trusted as the
        // cross-device identity (see SYNC-PLAN.md Phase 3) - the client
        // recomputes SyncKey itself from title/artist/album/duration, which must
        // land on the exact same value the server-side track's SyncKey would.
        Assert.Equal(Flower.Models.Track.BuildSyncKey("Come Together", "Beatles", "Abbey Road", 259), placeholder.SyncKey);
    }
}
