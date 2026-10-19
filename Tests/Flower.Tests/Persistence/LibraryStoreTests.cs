using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;

using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;

using Flower.Controls;
using Flower.Importer;
using Flower.Models;
using Flower.Persistence;
using Flower.Persistence.Sql;
using Flower.Tests.TestSupport;
using Flower.ViewModels;

using Xunit;

namespace Flower.Tests.Persistence;

// The library on disk: LibraryStore reading what the repository wrote, every
// Track field surviving SQLite, play-count bumps landing before they return,
// and a corrupt database being set aside rather than crashing startup.
[Collection("PlatformDataDirectory")]
public class LibraryStoreTests : IsolatedHome
{
    private static TrackRepository Repo() => new(FlowerDb.OpenDefault());
    private static TrackRepository Repo(FlowerDb db) => new(db);

    [Fact]
    public async Task LibraryStore_round_trips_tracks_including_duration()
    {
        var tracks = new List<Track>
        {
            new Track { Title = "A", Artists = "X", Duration = TimeSpan.FromSeconds(125), Path = "/music/a.mp3" },
        };

        Repo().ReplaceAll(tracks);
        var loaded = await new LibraryStore(NullLogger<LibraryStore>.Instance).LoadAsync();

        Assert.Single(loaded);
        Assert.Equal("A", loaded[0].Title);
        Assert.Equal(TimeSpan.FromSeconds(125), loaded[0].Duration);
    }

    // IsLocallyDownloaded decides whether the next rescan is allowed to drop a
    // track (see Library.UpdateTracks), so it is only any use if it survives a
    // restart - and the tracks it matters most for are the mobile ones in
    // app-private storage, on the platform most likely to be killed between
    // launches. The row mapper reads by ordinal, which makes an added column a
    // silent-corruption risk rather than a compile error, so pin both values.
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task LibraryStore_round_trips_whether_a_track_was_downloaded_here(bool downloaded)
    {
        Repo().ReplaceAll(new List<Track>
        {
            new Track { Title = "A", Path = "/private/downloads/a.mp3", IsLocallyDownloaded = downloaded },
        });

        var loaded = await new LibraryStore(NullLogger<LibraryStore>.Instance).LoadAsync();

        Assert.Equal(downloaded, Assert.Single(loaded).IsLocallyDownloaded);
    }

    [Fact]
    public async Task LibraryStore_Load_returns_empty_list_when_no_file_exists()
    {
        var loaded = await new LibraryStore(NullLogger<LibraryStore>.Instance).LoadAsync();
        Assert.Empty(loaded);
    }

    // Save (synchronous) is the Window.Closing counterpart to SaveAsync - see
    // its doc comment - so a track that just finished (incrementing PlayCount
    // via a fire-and-forget SaveAsync) isn't lost if the app quits before that
    // write lands. Must round-trip identically to the async path.
    [Fact]
    public void LibraryStore_Save_round_trips_tracks_synchronously()
    {
        var tracks = new List<Track>
        {
            new Track { Title = "A", Artists = "X", PlayCount = 1, Duration = TimeSpan.FromSeconds(125), Path = "/music/a.mp3" },
        };

        Repo().ReplaceAll(tracks);
        var loaded = new LibraryStore(NullLogger<LibraryStore>.Instance).Load();

        Assert.Single(loaded);
        Assert.Equal("A", loaded[0].Title);
        Assert.Equal(1, loaded[0].PlayCount);
    }

    // Minimal stand-in for GaplessAudioManager, just for raising EndReached below -
    // see PlaylistControlViewModelTests.FakeAudioManager for why that test
    // class never raises EndReached itself (needs a live Avalonia dispatcher).
    // This test avoids that by giving PlaylistControlViewModel an empty
    // current playlist, so there is no next track and the handler never
    // reaches its Dispatcher.UIThread.Post call - this test lives here (not
    // there) because it does touch LibraryStore for real and needs this
    // class's HOME redirection.
    private sealed class FakeAudioManager : Flower.Audio.IAudioManager
    {
        public bool IsPlaying { get; set; }
        public int Volume { get; set; }
        public int VolumeOffset { get; set; }
        public float Position { get; set; }
        public long Time { get; set; }
        public long Length { get; set; }
        public void Play(Track track, bool immediate = true) { }
        public void SetUpcoming(Track? next) { }
        public void Resume() { }
        public void Pause() { }
        public void Stop() { }
        public void ApplyEqualizer(Flower.Audio.Equalizer? equalizer) { }
        public void ApplyAudioTiming(Flower.Audio.AudioTimingSettings timing) { }
        public System.Collections.Generic.IReadOnlyList<Flower.Audio.AudioOutputDevice> GetOutputDevices() => [];
        public string? OutputDeviceId => null;
        public void SetOutputDevice(string? deviceId) { }
        public void RaiseEndReached() => EndReached?.Invoke(this, EventArgs.Empty);
#pragma warning disable CS0067
        public event EventHandler? Paused;
        public event EventHandler? Stopped;
        public event EventHandler? Playing;
        public event EventHandler? PositionChanged;
        public event EventHandler? VolumeChanged;
        public event EventHandler? EndReached;
        public event EventHandler<Flower.Audio.TrackFailedEventArgs>? TrackFailed;
        public event EventHandler? PlayedOut;
#pragma warning restore CS0067
    }

    // Regression test for the reported bug: play a track, it counts, but a
    // restart reverts it to 0. Root cause - every launch kicks off a
    // background rescan (App.axaml.cs) that replaces Library.Tracks with
    // brand-new Track instances for every file, even unchanged ones. If that
    // rescan lands while a track is still playing (plenty of time if e.g. the
    // user alt-tabs to Music.app and back), CurrentlyPlayingTrack is left
    // pointing at the old, now-discarded instance. Incrementing PlayCount on
    // that orphaned object used to be silently lost, since it's no longer in
    // Library.Tracks and never gets saved.
    [Fact]
    public void EndReached_increments_PlayCount_on_the_current_library_track_even_if_a_rescan_replaced_it_mid_playback()
    {
        var oldTrack = new Track { Title = "A", Path = "/music/a.mp3" };
        var library = new Library(new List<Track> { oldTrack });
        var emptyPlaylist = new MainPlaylist(new List<Track>());
        var audio = new FakeAudioManager();
        var vm = new PlaylistControlViewModel(
            audio, emptyPlaylist, library, new AppSettings(),
            new AppSettingsStore(NullLogger<AppSettingsStore>.Instance), NullLogger<PlaylistControlViewModel>.Instance);

        // The play-count write is deliberately handed off the decode
        // callback thread (see PlaylistControlViewModel.OffPlaybackThread);
        // run it inline so the assertion below isn't racing the pool.
        vm.OffPlaybackThread = work => work();
        // And the end of the track is handled where it would be on the UI
        // thread, which a plain test has no dispatcher running for.
        vm.OnUiThread = work => work();

        vm.Play(oldTrack);

        // Simulate a rescan landing while oldTrack is still "playing": a
        // brand-new Track instance for the same file replaces it in the library.
        var newTrack = new Track { Title = "A", Path = "/music/a.mp3" };
        library.UpdateTracks(new List<Track> { newTrack });

        audio.RaiseEndReached();

        Assert.Equal(1, newTrack.PlayCount);
        Assert.Equal(0, oldTrack.PlayCount);
    }

    // The SQLite counterpart of the quarantine case above: a database file
    // that isn't one must degrade to "empty library" rather than take the
    // process down. This runs from the DI factory on the startup path, before
    // there is any UI to report an error through, so an escaping exception is
    // a crash on launch with no way back - which is what it did until FlowerDb
    // learned to quarantine.
    [Fact]
    public void LibraryStore_Load_returns_empty_when_the_database_file_is_corrupt()
    {
        File.WriteAllText(FlowerDb.DefaultPath, "this is not a database");

        Assert.Empty(new LibraryStore(NullLogger<LibraryStore>.Instance).Load());
        Assert.True(File.Exists(FlowerDb.CorruptPath(FlowerDb.DefaultPath)));
    }

    // ...and the replacement database is a working one, not a second casualty.
    [Fact]
    public async Task A_quarantined_database_is_replaced_by_a_usable_one()
    {
        File.WriteAllText(FlowerDb.DefaultPath, "this is not a database");
        var store = new LibraryStore(NullLogger<LibraryStore>.Instance);

        Repo().ReplaceAll([new Track { Title = "After", Path = "/music/a.mp3" }]);

        Assert.Equal("After", Assert.Single(store.Load()).Title);
    }

    [Fact]
    public void A_play_count_bump_is_on_disk_before_it_returns()
    {
        var store = new LibraryStore(NullLogger<LibraryStore>.Instance);
        var track = new Track { Title = "A", Path = "/music/a.mp3" };
        Repo().ReplaceAll([track]);

        // Through Library, not through a store method: the write is Library's
        // own now (see its ITrackStore), so a caller cannot bump a count and
        // forget to persist it. Playing one song fires two of these -
        // RecordPlayed when it starts, IncrementPlayCount when it ends - and
        // each has to land on its own: this used to be debounced by 3s, and
        // anything that killed the process inside that window (a crash, or a
        // phone backgrounding the app, which had no flush hook at all)
        // silently dropped the increment.
        var library = new Library([track], NullLogger<Library>.Instance, new TrackRepository(FlowerDb.OpenDefault()));

        library.IncrementPlayCount(track);
        Assert.Equal(1, Assert.Single(store.Load()).PlayCount);

        library.IncrementPlayCount(track);
        Assert.Equal(2, Assert.Single(store.Load()).PlayCount);

        library.RecordPlayed(track);
        Assert.NotNull(Assert.Single(store.Load()).LastPlayedAt);
    }

    [Fact]
    public void A_play_count_bump_writes_one_row_and_leaves_the_rest_of_the_library_alone()
    {
        var store = new LibraryStore(NullLogger<LibraryStore>.Instance);
        var played = new Track { Title = "A", Path = "/music/a.mp3" };
        var untouched = new Track { Title = "B", Path = "/music/b.mp3", PlayCount = 7 };
        Repo().ReplaceAll([played, untouched]);

        var library = new Library([played, untouched], NullLogger<Library>.Instance, new TrackRepository(FlowerDb.OpenDefault()));
        library.IncrementPlayCount(played);

        var loaded = store.Load();
        Assert.Equal(1, loaded.Single(t => t.Path == "/music/a.mp3").PlayCount);
        Assert.Equal(7, loaded.Single(t => t.Path == "/music/b.mp3").PlayCount);
    }

    [Fact]
    public async Task Every_persisted_Track_field_round_trips_through_SQLite()
    {
        var store = new LibraryStore(NullLogger<LibraryStore>.Instance);
        var track = new Track
        {
            Title = "T", Subtitle = "S", Artists = "A", AlbumArtists = "AA", IsCompilation = true,
            Album = "Al", AlbumSort = "AlS", Year = "1999",
            TitleSort = "TS", ArtistsSort = "AS", ComposersSort = "CS",
            RememberPlaybackPosition = true, ResumePosition = TimeSpan.FromSeconds(90.5),
            IgnoreWhenShuffling = true, VolumeAdjustment = -35,
            TrackNumber = 3, TrackCount = 12, DiscNumber = 2, DiscCount = 2,
            Composers = "C", Conductor = "Cond", RemixedBy = "R",
            Genre = "G", BeatsPerMinute = 128, InitialKey = "Am", Grouping = "Gr",
            Publisher = "P", ISRC = "ISRC1",
            Comment = "Cm", Description = "D", Copyright = "Co", Lyrics = "L",
            Duration = TimeSpan.FromSeconds(123.456), Bitrate = 320, SampleRate = 44100,
            Channels = 2, BitsPerSample = 16, Codec = "flac", EncoderProfile = "LAME 3.100, VBR (V0)",
            Path = "/music/a.flac",
            OriginDeviceFingerprint = "fp", OriginTrackId = "otid",
            OriginFileExtension = "flac", OriginAlbumArtId = "al-art",
            PlayCount = 4, ImportedPlayCount = 7,
            LastPlayedAt = new DateTimeOffset(2026, 3, 4, 5, 6, 7, TimeSpan.Zero),
            DateAdded = new DateTimeOffset(2025, 1, 2, 3, 4, 5, TimeSpan.Zero),
        };
        track.RemotePlayCounts["peer-a"] = 11;
        track.RemotePlayCounts["peer-b"] = 22;

        Repo().ReplaceAll([track]);
        var reloaded = Assert.Single(store.Load());

        Assert.Equal(track.Id, reloaded.Id);
        Assert.Equal("T", reloaded.Title);
        Assert.Equal("S", reloaded.Subtitle);
        Assert.Equal("A", reloaded.Artists);
        Assert.Equal("AA", reloaded.AlbumArtists);
        Assert.True(reloaded.IsCompilation);
        Assert.Equal("Al", reloaded.Album);
        Assert.Equal("AlS", reloaded.AlbumSort);
        Assert.Equal("TS", reloaded.TitleSort);
        Assert.Equal("AS", reloaded.ArtistsSort);
        Assert.Equal("CS", reloaded.ComposersSort);
        Assert.True(reloaded.RememberPlaybackPosition);
        Assert.Equal(TimeSpan.FromSeconds(90.5), reloaded.ResumePosition);
        Assert.True(reloaded.IgnoreWhenShuffling);
        Assert.Equal(-35, reloaded.VolumeAdjustment);
        Assert.Equal("LAME 3.100, VBR (V0)", reloaded.EncoderProfile);
        Assert.Equal("1999", reloaded.Year);
        Assert.Equal(3u, reloaded.TrackNumber);
        Assert.Equal(12u, reloaded.TrackCount);
        Assert.Equal(2u, reloaded.DiscNumber);
        Assert.Equal(2u, reloaded.DiscCount);
        Assert.Equal("C", reloaded.Composers);
        Assert.Equal("Cond", reloaded.Conductor);
        Assert.Equal("R", reloaded.RemixedBy);
        Assert.Equal("G", reloaded.Genre);
        Assert.Equal(128u, reloaded.BeatsPerMinute);
        Assert.Equal("Am", reloaded.InitialKey);
        Assert.Equal("Gr", reloaded.Grouping);
        Assert.Equal("P", reloaded.Publisher);
        Assert.Equal("ISRC1", reloaded.ISRC);
        Assert.Equal("Cm", reloaded.Comment);
        Assert.Equal("D", reloaded.Description);
        Assert.Equal("Co", reloaded.Copyright);
        Assert.Equal("L", reloaded.Lyrics);
        Assert.Equal(track.Duration, reloaded.Duration);
        Assert.Equal(320, reloaded.Bitrate);
        Assert.Equal(44100, reloaded.SampleRate);
        Assert.Equal(2, reloaded.Channels);
        Assert.Equal(16, reloaded.BitsPerSample);
        Assert.Equal("flac", reloaded.Codec);
        Assert.Equal("/music/a.flac", reloaded.Path);
        Assert.Equal("fp", reloaded.OriginDeviceFingerprint);
        Assert.Equal("otid", reloaded.OriginTrackId);
        Assert.Equal("flac", reloaded.OriginFileExtension);
        Assert.Equal("al-art", reloaded.OriginAlbumArtId);
        Assert.Equal(4, reloaded.PlayCount);
        Assert.Equal(7, reloaded.ImportedPlayCount);
        Assert.Equal(track.LastPlayedAt, reloaded.LastPlayedAt);
        Assert.Equal(track.DateAdded, reloaded.DateAdded);
        Assert.Equal(11, reloaded.RemotePlayCounts["peer-a"]);
        Assert.Equal(22, reloaded.RemotePlayCounts["peer-b"]);
    }

    [Fact]
    public async Task Saving_the_library_removes_rows_for_tracks_no_longer_present()
    {
        var store = new LibraryStore(NullLogger<LibraryStore>.Instance);
        var kept = new Track { Title = "Kept", Path = "/music/kept.mp3" };
        var dropped = new Track { Title = "Dropped", Path = "/music/dropped.mp3" };

        Repo().ReplaceAll([kept, dropped]);
        Assert.Equal(2, store.Load().Count);

        // A rescan after the file was deleted on disk.
        Repo().ReplaceAll([kept]);

        Assert.Equal("Kept", Assert.Single(store.Load()).Title);
    }

    [Fact]
    public async Task A_removed_track_takes_its_remote_play_counts_with_it()
    {
        var store = new LibraryStore(NullLogger<LibraryStore>.Instance);
        var track = new Track { Title = "Gone", Path = "/music/gone.mp3" };
        track.RemotePlayCounts["peer"] = 5;
        Repo().ReplaceAll([track]);

        Repo().ReplaceAll([]);
        // Re-adding a track that reuses the id must not inherit the old rows -
        // the child table cascades on delete (see Schema.V1).
        var reused = new Track { Id = track.Id, Title = "Gone", Path = "/music/gone.mp3" };
        Repo().ReplaceAll([reused]);

        Assert.Empty(Assert.Single(store.Load()).RemotePlayCounts);
    }
}
