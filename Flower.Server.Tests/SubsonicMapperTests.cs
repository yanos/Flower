using Flower.Models;
using Flower.Server.Services;

namespace Flower.Server.Tests;

// What a served song carries, per field that a receiving device cannot
// reconstruct for itself.
//
// These used to be written against the app's own embedded host and its twin
// mapper, which is gone; SubsonicMapper.ToChild is the only implementation
// left, and it fills GET /api/flower/v1/library as well as the /rest browse
// endpoints - so a field missing here is a field missing from every synced
// placeholder on every client.
public class SubsonicMapperTests
{
    private static Track RealTrack(string title, string artist, string album, int durationSeconds = 200) =>
        new()
        {
            Title = title, Artists = artist, Album = album, TrackNumber = 1,
            Duration = TimeSpan.FromSeconds(durationSeconds), Year = "1999", Genre = "Rock",
            Path = $"/music/{title}.mp3",
        };

    // Track.RoundedSeconds, not a second inline Math.Round. A mapper that
    // truncated would report a Duration one second below SyncKey's own rounding
    // for any track whose fractional part is >= .5s - and SyncKey is what the
    // receiving side matches a placeholder against its own library by, so the
    // one track would fragment into two.
    [Fact]
    public void Duration_rounds_to_match_SyncKey_rather_than_truncating()
    {
        var track = new Track
        {
            Title = "Mata Zyklek", Artists = "Angine de Poitrine", Album = "Vol.II",
            Duration = TimeSpan.FromSeconds(369.888), Path = "/music/Mata Zyklek.mp3",
        };
        Assert.EndsWith("|370", track.SyncKey); // Sanity check on the premise itself.

        Assert.Equal(370, SubsonicMapper.ToChild(track).Duration);
    }

    // The serving half of what Flower.Tests' LibrarySyncMapperTests reads back.
    // Both sides of the wire have to agree or the fields simply never arrive -
    // which is what happened to the technical fields for a whole release.
    [Fact]
    public void The_sort_tags_the_playback_options_and_the_encoder_profile_are_sent()
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

        var song = SubsonicMapper.ToChild(track);

        Assert.Equal("Come Together", song.SortTitle);
        Assert.Equal("Beatles, The", song.SortArtist);
        Assert.Equal("Abbey Road", song.SortAlbum);
        Assert.Equal("Lennon, John", song.SortComposer);
        Assert.True(song.RememberPlaybackPosition);
        Assert.Equal(754, song.ResumePositionSeconds);
        Assert.True(song.IgnoreWhenShuffling);
        Assert.Equal(-20, song.VolumeAdjustment);
        Assert.Equal("LAME 3.100, VBR (V0)", song.EncoderProfile);
    }

    // The downloading side names its saved file with this, so a leading dot or
    // a stray case difference lands the track at "song..MP3".
    [Fact]
    public void Suffix_is_the_local_file_extension_lowercased_and_without_a_leading_dot()
    {
        var track = RealTrack("Come Together", "Beatles", "Abbey Road");
        track.Path = "/music/Come Together.MP3";

        Assert.Equal("mp3", SubsonicMapper.ToChild(track).Suffix);
    }

    [Fact]
    public void The_song_id_is_the_tracks_own_stable_Id_not_its_tag_derived_SyncKey()
    {
        var track = RealTrack("Come Together", "Beatles", "Abbey Road", durationSeconds: 259);

        var song = SubsonicMapper.ToChild(track);

        Assert.Equal(track.Id.ToKey(), song.Id);
        Assert.NotEqual(track.SyncKey, song.Id);
    }

    // SyncKey is derived from Title/Artist/Album and a rounded duration, so
    // serving it as the song id meant a tag edit here invalidated every id a
    // client was still holding - its next stream or download request 404'd,
    // indistinguishable from the server being unreachable.
    [Fact]
    public void The_song_id_survives_a_tag_edit_on_the_serving_device()
    {
        var track = RealTrack("Come Together", "Beatles", "Abbey Road", durationSeconds: 259);
        var before = SubsonicMapper.ToChild(track).Id;

        track.Title = "Come Together (Remastered)";
        var after = SubsonicMapper.ToChild(track).Id;

        Assert.Equal(before, after);
        Assert.NotEqual(track.SyncKey, after); // Sanity check on the premise: SyncKey did move.
    }

    [Fact]
    public void PlayCounts_includes_our_own_tally_under_our_own_fingerprint()
    {
        var track = RealTrack("Come Together", "Beatles", "Abbey Road");
        track.PlayCount = 3;
        track.ImportedPlayCount = 4;

        Assert.Equal(7, SubsonicMapper.ToChild(track, "self-1").PlayCounts!["self-1"]);
    }

    [Fact]
    public void PlayCounts_carries_forward_every_other_device_this_track_already_knows_about()
    {
        var track = RealTrack("Come Together", "Beatles", "Abbey Road");
        track.RemotePlayCounts = new Dictionary<string, int> { ["peer-2"] = 12 };

        Assert.Equal(12, SubsonicMapper.ToChild(track, "self-1").PlayCounts!["peer-2"]);
    }

    // Without a fingerprint to name the tally, no counts are sent at all - what
    // the /rest browse endpoints do, and what a third-party client expects.
    [Fact]
    public void No_play_counts_are_sent_when_there_is_nobody_to_attribute_them_to()
    {
        var track = RealTrack("Come Together", "Beatles", "Abbey Road");
        track.PlayCount = 3;

        Assert.Null(SubsonicMapper.ToChild(track).PlayCounts);
    }

    // A song's artist id has to point at an artist the album listing mentions,
    // which for a compilation is the album artist and never the track's own.
    [Fact]
    public void A_compilation_tracks_album_and_artist_ids_come_from_the_album_artist()
    {
        var track = new Track
        {
            Title = "One", Artists = "Artist A", AlbumArtists = "Various Artists",
            Album = "Compilation", Path = "/music/one.mp3",
        };

        var song = SubsonicMapper.ToChild(track);

        Assert.Equal(Flower.Services.SubsonicIdentity.AlbumId("Various Artists", "Compilation"), song.AlbumId);
        Assert.Equal(Flower.Services.SubsonicIdentity.ArtistId("Various Artists"), song.ArtistId);
        Assert.Equal("Various Artists", song.DisplayAlbumArtist);
    }
}
