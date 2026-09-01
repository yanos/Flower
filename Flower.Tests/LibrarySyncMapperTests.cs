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

    // The Technical tab of Track Info reads these five, and a library made
    // entirely of synced placeholders had none of them: the manifest carried
    // BitRate alone, and the mapper did not read even that. See Child's own
    // comment on SamplingRate.
    [Fact]
    public void ToPlaceholderTrack_carries_the_technical_fields_the_origin_scanned()
    {
        var song = new Child(
            Id: "some-id", Title: "Come Together", Album: "Abbey Road", Artist: "Beatles",
            AlbumId: "al:1", ArtistId: "ar:1", Track: 1, Year: 1969, Genre: "Rock",
            Size: null, ContentType: null, Suffix: "flac", Duration: 259, BitRate: 990, CoverArt: null,
            SamplingRate: 44100, ChannelCount: 2, BitDepth: 24, Codec: "Flac");

        var track = LibrarySyncMapper.ToPlaceholderTrack(song, "peer-1", "self-1");

        Assert.Equal(990, track.Bitrate);
        Assert.Equal(44100, track.SampleRate);
        Assert.Equal(2, track.Channels);
        Assert.Equal(24, track.BitsPerSample);
        Assert.Equal("Flac", track.Codec);
    }

    // A third-party OpenSubsonic server sends no Codec at all, and may send
    // none of the numbers either. Those must land as the same "unset" an
    // unscanned file has, not as a confident zero the Technical tab would
    // render as "0 kHz".
    [Fact]
    public void ToPlaceholderTrack_leaves_absent_technical_fields_unset()
    {
        var song = new Child(
            Id: "some-id", Title: "Come Together", Album: "Abbey Road", Artist: "Beatles",
            AlbumId: "al:1", ArtistId: "ar:1", Track: 1, Year: 1969, Genre: "Rock",
            Size: null, ContentType: null, Suffix: "mp3", Duration: 259, BitRate: null, CoverArt: null);

        var track = LibrarySyncMapper.ToPlaceholderTrack(song, "peer-1", "self-1");

        Assert.Equal(0, track.Bitrate);
        Assert.Equal(0, track.SampleRate);
        Assert.Equal(0, track.Channels);
        Assert.Equal(0, track.BitsPerSample);
        Assert.Null(track.Codec);
    }

    // What the download names the saved file after, so a downloaded library is
    // browsable outside Flower - see Child.RelativePath and
    // LibraryDownloadService.ResolveDestination.
    [Fact]
    public void ToPlaceholderTrack_keeps_the_origins_relative_path_for_the_download_to_name_the_file_after()
    {
        var song = new Child(
            Id: "some-id", Title: "Fabienk", Album: "Vol.II", Artist: "Angine de Poitrine",
            AlbumId: "al:1", ArtistId: "ar:1", Track: 1, Year: null, Genre: null,
            Size: null, ContentType: null, Suffix: "mp3", Duration: 259, BitRate: null, CoverArt: null,
            RelativePath: "Angine de Poitrine/Vol.II/01 Fabienk.mp3");

        var track = LibrarySyncMapper.ToPlaceholderTrack(song, "peer-1", "self-1");

        Assert.Equal("Angine de Poitrine/Vol.II/01 Fabienk.mp3", track.OriginRelativePath);
    }

    // A third-party OpenSubsonic server sends no such field, and the download
    // falls back to the track id plus Suffix, exactly as it always did.
    [Fact]
    public void ToPlaceholderTrack_leaves_the_relative_path_unset_when_the_server_sends_none()
    {
        var song = new Child(
            Id: "some-id", Title: "Fabienk", Album: "Vol.II", Artist: "Angine de Poitrine",
            AlbumId: "al:1", ArtistId: "ar:1", Track: 1, Year: null, Genre: null,
            Size: null, ContentType: null, Suffix: "mp3", Duration: 259, BitRate: null, CoverArt: null);

        Assert.Null(LibrarySyncMapper.ToPlaceholderTrack(song, "peer-1", "self-1").OriginRelativePath);
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

    // The regression these three cover: AlbumArtists and IsCompilation had no
    // field on Child at all, so a synced placeholder recomputed
    // EffectiveAlbumArtist from two empty fields and always landed on the
    // per-track Artists. Every various-artists compilation therefore fragmented
    // into one album tile per contributing artist on the receiving side, while
    // the sender showed a single album - measured on a real 16k-track library
    // as 30 albums, one of them a 31-artist compilation showing as 31 tiles.
    // What matters in each case is that EffectiveAlbumArtist agrees with the
    // sender's, since that is what every album grouping keys on.
    [Fact]
    public void ToPlaceholderTrack_restores_the_album_artist_of_a_compilation_tagged_only_with_the_flag()
    {
        // A blank AlbumArtists tag plus the compilation flag - the sender's
        // EffectiveAlbumArtist resolved this to "Various Artists".
        var song = new Child(
            Id: "some-id", Title: "Sinnerman", Album: "Kill Bill Volume 1", Artist: "Nina Simone",
            AlbumId: "al:1", ArtistId: "ar:1", Track: 3, Year: 2003, Genre: null,
            Size: null, ContentType: null, Suffix: "mp3", Duration: 120, BitRate: null, CoverArt: null,
            DisplayAlbumArtist: "Various Artists", IsCompilation: true);

        var track = LibrarySyncMapper.ToPlaceholderTrack(song, "peer-1", "self-1");

        Assert.Equal("Nina Simone", track.Artists);
        Assert.True(track.IsCompilation);
        Assert.Equal("Various Artists", track.EffectiveAlbumArtist);
    }

    [Fact]
    public void ToPlaceholderTrack_restores_an_explicit_album_artist_tag()
    {
        var song = new Child(
            Id: "some-id", Title: "Blue In Green", Album: "Kind Of Blue", Artist: "Miles Davis & Bill Evans",
            AlbumId: "al:1", ArtistId: "ar:1", Track: 3, Year: 1959, Genre: null,
            Size: null, ContentType: null, Suffix: "mp3", Duration: 337, BitRate: null, CoverArt: null,
            DisplayAlbumArtist: "Miles Davis", IsCompilation: false);

        var track = LibrarySyncMapper.ToPlaceholderTrack(song, "peer-1", "self-1");

        Assert.Equal("Miles Davis", track.AlbumArtists);
        Assert.Equal("Miles Davis", track.EffectiveAlbumArtist);
    }

    // The sender's fallback ends at Artists for an ordinary album, so there is
    // nothing to store - copying the display value in anyway would stamp a
    // redundant AlbumArtists tag onto most of a library. EffectiveAlbumArtist
    // still has to come out the same.
    [Fact]
    public void ToPlaceholderTrack_stores_no_album_artist_when_it_only_repeats_the_track_artist()
    {
        var song = new Child(
            Id: "some-id", Title: "Come Together", Album: "Abbey Road", Artist: "Beatles",
            AlbumId: "al:1", ArtistId: "ar:1", Track: 1, Year: 1969, Genre: "Rock",
            Size: null, ContentType: null, Suffix: "mp3", Duration: 259, BitRate: null, CoverArt: null,
            DisplayAlbumArtist: "Beatles", IsCompilation: false);

        var track = LibrarySyncMapper.ToPlaceholderTrack(song, "peer-1", "self-1");

        Assert.Null(track.AlbumArtists);
        Assert.False(track.IsCompilation);
        Assert.Equal("Beatles", track.EffectiveAlbumArtist);
    }

    // A third-party OpenSubsonic server that sends neither field must still map
    // cleanly, the same way a missing DateAdded/LastPlayed already does.
    [Fact]
    public void ToPlaceholderTrack_tolerates_a_server_that_sends_no_album_artist_at_all()
    {
        var song = new Child(
            Id: "some-id", Title: "Come Together", Album: "Abbey Road", Artist: "Beatles",
            AlbumId: "al:1", ArtistId: "ar:1", Track: 1, Year: 1969, Genre: "Rock",
            Size: null, ContentType: null, Suffix: "mp3", Duration: 259, BitRate: null, CoverArt: null);

        var track = LibrarySyncMapper.ToPlaceholderTrack(song, "peer-1", "self-1");

        Assert.Null(track.AlbumArtists);
        Assert.False(track.IsCompilation);
        Assert.Equal("Beatles", track.EffectiveAlbumArtist);
    }

    // The sort tags and the four playback options. The tags are in the file, so
    // a device that downloads it rescans them anyway - the placeholder in
    // between is what would otherwise file "The Beatles" under T on one device
    // and B on the other. The options are in no file at all, so this is the
    // only road they have.
    [Fact]
    public void ToPlaceholderTrack_carries_the_sort_tags_and_the_playback_options()
    {
        var song = new Child(
            Id: "some-id", Title: "Come Together", Album: "Abbey Road", Artist: "The Beatles",
            AlbumId: "al:1", ArtistId: "ar:1", Track: 1, Year: 1969, Genre: "Rock",
            Size: null, ContentType: null, Suffix: "mp3", Duration: 259, BitRate: null, CoverArt: null,
            SortTitle: "Come Together", SortArtist: "Beatles, The",
            SortAlbum: "Abbey Road", SortComposer: "Lennon, John",
            RememberPlaybackPosition: true, ResumePositionSeconds: 754,
            IgnoreWhenShuffling: true, VolumeAdjustment: -20,
            EncoderProfile: "LAME 3.100, VBR (V0)");

        var track = LibrarySyncMapper.ToPlaceholderTrack(song, "peer-1", "self-1");

        Assert.Equal("Come Together", track.TitleSort);
        Assert.Equal("Beatles, The", track.ArtistsSort);
        Assert.Equal("Abbey Road", track.AlbumSort);
        Assert.Equal("Lennon, John", track.ComposersSort);
        Assert.True(track.RememberPlaybackPosition);
        Assert.Equal(TimeSpan.FromSeconds(754), track.ResumePosition);
        Assert.True(track.IgnoreWhenShuffling);
        Assert.Equal(-20, track.VolumeAdjustment);
        Assert.Equal("LAME 3.100, VBR (V0)", track.EncoderProfile);
        // And "sorts as itself" survives as itself - a blank override would put
        // the track above everything (see Track.SortAs).
        Assert.Equal("Beatles, The", track.ArtistsSortValue);
    }

    // A third-party server sends none of them; that must read as "not
    // configured", which is exactly the default of each.
    [Fact]
    public void ToPlaceholderTrack_leaves_the_options_at_their_defaults_when_the_server_sends_none()
    {
        var song = new Child(
            Id: "some-id", Title: "Come Together", Album: "Abbey Road", Artist: "The Beatles",
            AlbumId: "al:1", ArtistId: "ar:1", Track: 1, Year: 1969, Genre: "Rock",
            Size: null, ContentType: null, Suffix: "mp3", Duration: 259, BitRate: null, CoverArt: null);

        var track = LibrarySyncMapper.ToPlaceholderTrack(song, "peer-1", "self-1");

        Assert.Null(track.TitleSort);
        Assert.Null(track.ArtistsSort);
        Assert.False(track.RememberPlaybackPosition);
        Assert.Null(track.ResumePosition);
        Assert.False(track.IgnoreWhenShuffling);
        Assert.Equal(0, track.VolumeAdjustment);
        Assert.Null(track.EncoderProfile);
        // Falls back to the display value, so it still sorts under "The".
        Assert.Equal("The Beatles", track.ArtistsSortValue);
    }
}
