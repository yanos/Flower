using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;

using Avalonia.Headless.XUnit;
using Avalonia.Threading;

using Flower.Manager;
using Flower.Models;
using Flower.Persistence;
using Flower.Services;
using Flower.Tests.TestSupport;
using Flower.ViewModels;

using Material.Icons;

using Microsoft.Extensions.Logging.Abstractions;

using Xunit;

namespace Flower.Tests;

// docs/ARCHITECTURE-REVIEW.md §5.8: the small ViewModels that had no
// dedicated tests at all. Grouped in one file because individually each is a
// handful of assertions over display formatting, property-change fan-out and
// event forwarding - the kind of thing that is silently wrong for a release
// rather than crashing.
//
// LogViewModel and ScreenStackPanel from that same item are large enough to
// own their own files (LogViewModelTests, ScreenStackPanelSwipeTests).
public class TrackRowViewModelTests
{
    private static TrackRowViewModel Row(Track track, bool first = true, int groupSize = 1) =>
        new() { Track = track, IsFirstInAlbumGroup = first, AlbumGroupSize = groupSize };

    // Art is capped so a short album's image never bleeds down into the next
    // group, and is proportionally smaller for 1-2 track albums.
    [Theory]
    [InlineData(1, 28.0)]
    [InlineData(2, 56.0)]
    [InlineData(3, 76.0)]  // 3*28 = 84, capped at ArtMaxSize
    [InlineData(20, 76.0)]
    public void AlbumArtDisplaySize_is_the_group_height_capped_at_ArtMaxSize(int groupSize, double expected)
    {
        Assert.Equal(expected, Row(new Track(), groupSize: groupSize).AlbumArtDisplaySize);
    }

    [Fact]
    public void TrackNumberDisplay_is_blank_when_there_is_no_track_number()
    {
        Assert.Equal("", Row(new Track { TrackNumber = 0 }).TrackNumberDisplay);
        Assert.Equal("7", Row(new Track { TrackNumber = 7 }).TrackNumberDisplay);
    }

    // TotalPlayCount sums Flower's own count, the iTunes import and every
    // synced device's - a zero total shows as blank rather than "0".
    [Fact]
    public void PlayCountDisplay_is_blank_at_zero_and_shows_the_total_otherwise()
    {
        Assert.Equal("", Row(new Track()).PlayCountDisplay);

        var played = new Track { PlayCount = 2, ImportedPlayCount = 3 };
        Assert.Equal(played.TotalPlayCount.ToString(), Row(played).PlayCountDisplay);
    }

    [Fact]
    public void LastPlayedDisplay_is_blank_for_a_never_played_track()
    {
        Assert.Equal("", Row(new Track { LastPlayedAt = null }).LastPlayedDisplay);
        Assert.NotEqual("", Row(new Track { LastPlayedAt = DateTimeOffset.UtcNow }).LastPlayedDisplay);
    }

    // Both display strings read straight off Track, which is not
    // INotifyPropertyChanged, so a play-count bump has to be pushed in.
    [Fact]
    public void NotifyStatsChanged_raises_both_derived_display_properties()
    {
        var row = Row(new Track());
        var raised = new List<string?>();
        row.PropertyChanged += (_, e) => raised.Add(e.PropertyName);

        row.NotifyStatsChanged();

        Assert.Contains(nameof(TrackRowViewModel.PlayCountDisplay), raised);
        Assert.Contains(nameof(TrackRowViewModel.LastPlayedDisplay), raised);
    }

    [Theory]
    [InlineData(0, 0, 5, "0:05")]
    [InlineData(0, 3, 7, "3:07")]
    [InlineData(0, 59, 59, "59:59")]
    [InlineData(1, 2, 3, "1:02:03")]   // switches to h:mm:ss only past an hour
    public void DurationDisplay_only_shows_hours_when_there_are_any(int h, int m, int s, string expected)
    {
        Assert.Equal(expected, Row(new Track { Duration = new TimeSpan(h, m, s) }).DurationDisplay);
    }

    // A placeholder is a synced track with no local file yet.
    [Fact]
    public void IsPlaceholder_is_driven_by_a_null_Path()
    {
        Assert.True(Row(new Track { Path = null }).IsPlaceholder);
        Assert.False(Row(new Track { Path = "/music/a.mp3" }).IsPlaceholder);
    }

    // IsAvailable defaults to false so a just-built row never flashes as
    // available before it is actually known to be.
    [Fact]
    public void A_new_placeholder_row_starts_unavailable_and_not_downloadable()
    {
        var row = Row(new Track { Path = null });

        Assert.False(row.IsAvailable);
        Assert.True(row.IsUnavailable);
        Assert.False(row.IsDownloadable);
    }

    [Fact]
    public void Setting_IsAvailable_flips_both_derived_flags_and_raises_them()
    {
        var row = Row(new Track { Path = null });
        var raised = new List<string?>();
        row.PropertyChanged += (_, e) => raised.Add(e.PropertyName);

        row.IsAvailable = true;

        Assert.False(row.IsUnavailable);
        Assert.True(row.IsDownloadable);
        Assert.Contains(nameof(TrackRowViewModel.IsUnavailable), raised);
        Assert.Contains(nameof(TrackRowViewModel.IsDownloadable), raised);
    }

    // A real (non-placeholder) track is never "unavailable" or "downloadable",
    // whatever IsAvailable happens to say.
    // Both flags are gated on IsPlaceholder, so an ordinary local track is
    // never either one - including in the IsAvailable=false default state,
    // which is the case that tells "gated on IsPlaceholder" apart from a bare
    // "!IsAvailable".
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void A_local_track_is_neither_unavailable_nor_downloadable(bool available)
    {
        var row = Row(new Track { Path = "/music/a.mp3" });
        row.IsAvailable = available;

        Assert.False(row.IsUnavailable);
        Assert.False(row.IsDownloadable);
    }

    [Fact]
    public void Setting_IsAvailable_to_its_current_value_raises_nothing()
    {
        var row = Row(new Track { Path = null });
        row.IsAvailable = true;

        var raised = 0;
        row.PropertyChanged += (_, _) => raised++;
        row.IsAvailable = true;

        Assert.Equal(0, raised);
    }

    [Fact]
    public void IsDownloadIdle_is_false_while_downloading_and_after_a_failure()
    {
        var row = Row(new Track { Path = null });
        Assert.True(row.IsDownloadIdle);

        row.IsDownloadUnavailable = true;
        Assert.False(row.IsDownloadIdle);

        row.IsDownloadUnavailable = false;
        Assert.True(row.IsDownloadIdle);
    }

    // The spinner's timer is owned by the row, not the recycled container -
    // and a row discarded mid-download used to leave a 16ms DispatcherTimer
    // registered forever, one per orphaned row.
    // Lets the row's real 16ms DispatcherTimer come due, then pumps it. Just
    // calling RunJobs immediately proves nothing: the timer would not have
    // fired yet either way.
    // Runs the dispatcher's real main loop for long enough that the row's 16ms
    // DispatcherTimer actually comes due and fires. RunJobs() alone is not
    // enough - it drains queued operations but never advances the timer queue,
    // so the spinner would read as "not turning" whether or not the timer was
    // still alive, and the test would assert nothing.
    private static void LetTheSpinnerTick(int milliseconds = 120)
    {
        using var cts = new CancellationTokenSource(milliseconds);
        Dispatcher.UIThread.MainLoop(cts.Token);
    }

    [AvaloniaFact]
    public void The_spinner_turns_while_downloading()
    {
        var row = Row(new Track { Path = null });
        row.IsDownloading = true;

        LetTheSpinnerTick();

        Assert.True(row.SpinAngle > 0, "the spinner never advanced while downloading");
    }

    // A row discarded mid-download used to leave its timer registered on the
    // dispatcher forever, with the Tick closure keeping the ViewModel alive and
    // burning a 60fps wakeup - one per orphaned row of a batch download.
    [AvaloniaFact]
    public void Dispose_stops_the_download_spinner_and_resets_its_angle()
    {
        var row = Row(new Track { Path = null });
        row.IsDownloading = true;
        LetTheSpinnerTick();

        row.Dispose();
        Assert.Equal(0, row.SpinAngle);

        LetTheSpinnerTick();
        Assert.Equal(0, row.SpinAngle);
    }

    [AvaloniaFact]
    public void Clearing_IsDownloading_stops_the_spinner_and_resets_its_angle()
    {
        var row = Row(new Track { Path = null });
        row.IsDownloading = true;
        LetTheSpinnerTick();

        row.IsDownloading = false;
        Assert.Equal(0, row.SpinAngle);

        LetTheSpinnerTick();
        Assert.Equal(0, row.SpinAngle);
        Assert.True(row.IsDownloadIdle);
    }
}

public class VolumeControlViewModelTests
{
    // The ViewModel holds no volume of its own - it is a pass-through to the
    // audio manager, which is what makes the volume slider agree with whatever
    // the OS/mixer last set.
    [Fact]
    public void Volume_reads_and_writes_straight_through_to_the_audio_manager()
    {
        var audio = new FakeAudioManager { Volume = 42 };
        var vm = new VolumeControlViewModel(audio);

        Assert.Equal(42, vm.Volume);

        vm.Volume = 80;
        Assert.Equal(80, audio.Volume);

        audio.Volume = 15;
        Assert.Equal(15, vm.Volume);
    }
}

public class SidebarItemTests
{
    [Fact]
    public void A_header_row_is_not_selectable_and_every_other_kind_is()
    {
        Assert.True(new SidebarItem(SidebarItemKind.Header, "Library").IsHeader);
        Assert.False(new SidebarItem(SidebarItemKind.Header, "Library").IsSelectable);

        foreach (var kind in Enum.GetValues<SidebarItemKind>().Where(k => k != SidebarItemKind.Header))
        {
            Assert.False(new SidebarItem(kind, kind.ToString()).IsHeader);
            Assert.True(new SidebarItem(kind, kind.ToString()).IsSelectable);
        }
    }

    // Both reachability glyphs are gated on IsPairedServer: an ordinary
    // discovered-device row shows neither, whatever IsReachable says.
    [Fact]
    public void Reachability_icons_only_show_for_the_paired_server_row()
    {
        var ordinary = new SidebarItem(SidebarItemKind.Device, "Some Phone") { IsReachable = true };
        Assert.False(ordinary.ShowReachableIcon);
        Assert.False(ordinary.ShowUnreachableIcon);

        var paired = new SidebarItem(SidebarItemKind.Device, "Server") { IsPairedServer = true };
        Assert.False(paired.ShowReachableIcon);
        Assert.True(paired.ShowUnreachableIcon);   // pinned but not reachable right now

        paired.IsReachable = true;
        Assert.True(paired.ShowReachableIcon);
        Assert.False(paired.ShowUnreachableIcon);
    }

    // Both setters have to fan out to the two computed glyph properties, or the
    // icon silently stops tracking reachability.
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Setting_IsPairedServer_or_IsReachable_raises_both_glyph_properties(bool viaReachable)
    {
        var item = new SidebarItem(SidebarItemKind.Device, "Server");
        var raised = new List<string?>();
        item.PropertyChanged += (_, e) => raised.Add(e.PropertyName);

        if (viaReachable)
            item.IsReachable = true;
        else
            item.IsPairedServer = true;

        Assert.Contains(nameof(SidebarItem.ShowReachableIcon), raised);
        Assert.Contains(nameof(SidebarItem.ShowUnreachableIcon), raised);
    }

    // Icon and Device are settable rather than init-only precisely so a device
    // row can be re-pointed in place - see MainViewModel.FindDeviceSidebarItem
    // and RelocateDeviceSidebarItemIfNeeded.
    [Fact]
    public void Icon_Device_Name_and_Subtitle_are_mutable_and_notify()
    {
        var item = new SidebarItem(SidebarItemKind.Device, "Phone", MaterialIconKind.Cellphone);
        var raised = new List<string?>();
        item.PropertyChanged += (_, e) => raised.Add(e.PropertyName);

        item.Icon     = MaterialIconKind.Server;
        item.Name     = "Desktop";
        item.Subtitle = "192.168.1.7";

        Assert.Equal(MaterialIconKind.Server, item.Icon);
        Assert.Equal("Desktop", item.Name);
        Assert.Equal("192.168.1.7", item.Subtitle);
        Assert.Equal(new[] { nameof(SidebarItem.Icon), nameof(SidebarItem.Name), nameof(SidebarItem.Subtitle) },
                     raised.ToArray());
    }

    [Fact]
    public void A_playlist_row_carries_its_playlist()
    {
        var playlist = new Playlist("Mix", new List<Track>());
        var item = new SidebarItem(SidebarItemKind.Playlist, "Mix", MaterialIconKind.PlaylistMusic, playlist);

        Assert.Same(playlist, item.Playlist);
        Assert.Null(item.Device);
    }
}

[Collection("PlatformDataDirectory")]
public class EqualizerViewModelTests : PinnedDataDirectory
{
    private static EqualizerViewModel Make(out FakeAudioManager audio, AppSettings? settings = null)
    {
        audio = new FakeAudioManager();
        return new EqualizerViewModel(audio, settings ?? new AppSettings(),
                                      new AppSettingsStore(NullLogger<AppSettingsStore>.Instance));
    }

    [Fact]
    public void It_builds_one_band_per_equalizer_band_labelled_by_centre_frequency()
    {
        var vm = Make(out _);

        Assert.Equal(Equalizer.BandCount, vm.Bands.Count);
        Assert.Equal("31", vm.Bands[0].FrequencyLabel);
        Assert.Equal("16k", vm.Bands[^1].FrequencyLabel);
    }

    // First run: no EqualizerSettings at all. The ViewModel installs a flat,
    // disabled default onto AppSettings rather than leaving it null.
    [Fact]
    public void First_run_installs_flat_disabled_defaults_onto_AppSettings()
    {
        var settings = new AppSettings { EqualizerSettings = null };
        var vm = Make(out _, settings);

        Assert.NotNull(settings.EqualizerSettings);
        Assert.False(vm.Enabled);
        Assert.Equal(Equalizer.BandCount, settings.EqualizerSettings!.BandGainsDb.Length);
        Assert.All(vm.Bands, b => Assert.Equal(0, b.GainDb));
    }

    // Defensive re-size against a hand-edited settings.json - the alternative
    // is indexing BandGainsDb out of range on the first slider move.
    [Theory]
    [InlineData(0)]
    [InlineData(3)]
    [InlineData(64)]
    public void A_wrong_length_band_array_is_replaced_rather_than_indexed_out_of_range(int length)
    {
        var settings = new AppSettings
        {
            EqualizerSettings = new EqualizerSettings { BandGainsDb = new double[length] },
        };
        var vm = Make(out _, settings);

        Assert.Equal(Equalizer.BandCount, settings.EqualizerSettings!.BandGainsDb.Length);
        vm.Bands[Equalizer.BandCount - 1].GainDb = 6; // would throw if the array were still short
        Assert.Equal(6, vm.Bands[Equalizer.BandCount - 1].GainDb);
    }

    [Fact]
    public void Saved_settings_are_restored_rather_than_reset()
    {
        var gains = new double[Equalizer.BandCount];
        gains[4] = -7.5;
        var settings = new AppSettings
        {
            EqualizerSettings = new EqualizerSettings { Enabled = true, PreampDb = 3, BandGainsDb = gains },
        };
        var vm = Make(out _, settings);

        Assert.True(vm.Enabled);
        Assert.Equal(3, vm.PreampDb);
        Assert.Equal(-7.5, vm.Bands[4].GainDb);
    }

    // Live-apply: there is no Apply button, so every mutation has to push an
    // Equalizer through immediately.
    [Fact]
    public void Enabling_pushes_an_equalizer_and_disabling_clears_it()
    {
        var vm = Make(out var audio);

        vm.Enabled = true;
        Assert.NotNull(audio.LastAppliedEqualizer);

        // True bypass, not an all-zero-dB filter still in the signal path.
        vm.Enabled = false;
        Assert.Null(audio.LastAppliedEqualizer);
    }

    [Fact]
    public void Changing_a_band_or_the_preamp_while_enabled_pushes_a_rebuilt_equalizer()
    {
        var vm = Make(out var audio);
        vm.Enabled = true;
        var first = audio.LastAppliedEqualizer;

        vm.Bands[2].GainDb = 4;
        var afterBand = audio.LastAppliedEqualizer;
        Assert.NotSame(first, afterBand);

        vm.PreampDb = -2;
        Assert.NotSame(afterBand, audio.LastAppliedEqualizer);
    }

    // A band moved while the EQ is off must still be remembered, and must not
    // sneak an equalizer back into the signal path.
    [Fact]
    public void Changing_a_band_while_disabled_is_remembered_but_applies_nothing()
    {
        var vm = Make(out var audio);

        vm.Bands[1].GainDb = 5;

        Assert.Equal(5, vm.Bands[1].GainDb);
        Assert.Null(audio.LastAppliedEqualizer);
    }

    [Fact]
    public void A_band_reads_and_writes_only_its_own_slot()
    {
        var vm = Make(out _);

        vm.Bands[3].GainDb = 9;

        Assert.Equal(9, vm.Bands[3].GainDb);
        Assert.All(vm.Bands.Where((_, i) => i != 3), b => Assert.Equal(0, b.GainDb));
    }

    [Fact]
    public void Setting_a_band_notifies_so_the_slider_stays_bound()
    {
        var vm = Make(out _);
        var raised = 0;
        vm.Bands[0].PropertyChanged += (_, _) => raised++;

        vm.Bands[0].GainDb = 2;

        Assert.Equal(1, raised);
    }
}

[Collection("PlatformDataDirectory")]
public class CurrentlyPlayingControlViewModelTests : PinnedDataDirectory
{
    private static CurrentlyPlayingControlViewModel Make(
        out FakeAudioManager audio, out PlaylistControlViewModel playback, params Track[] tracks)
    {
        audio = new FakeAudioManager();
        var library = new Library(tracks.ToList());
        playback = new PlaylistControlViewModel(
            audio, new MainPlaylist(tracks.ToList()), library, new AppSettings(),
            new LibraryStore(NullLogger<LibraryStore>.Instance),
            new AppSettingsStore(NullLogger<AppSettingsStore>.Instance),
            NullLogger<PlaylistControlViewModel>.Instance);
        return new CurrentlyPlayingControlViewModel(
            playback, audio, library, NullLogger<CurrentlyPlayingControlViewModel>.Instance);
    }

    private static Track T(string title) => new()
    {
        Title = title, Path = $"/music/{title}.mp3", Artists = "An Artist", Album = "An Album", Year = "1999",
        Duration = TimeSpan.FromSeconds(215),
    };

    // Deliberately a single space, not empty and not hidden: the control is
    // always rendered so its height stays constant whether or not anything is
    // playing.
    [Fact]
    public void Subtitle_is_a_single_space_with_nothing_playing()
    {
        var vm = Make(out _, out _);

        Assert.Equal(" ", vm.Subtitle);
        Assert.Null(vm.CurrentlyPlayingTrack);
    }

    [AvaloniaFact]
    public void Subtitle_is_artist_album_and_year_once_a_track_is_playing()
    {
        var track = T("A");
        var vm = Make(out _, out var playback, track);

        playback.Play(track);

        Assert.Equal("An Artist — An Album (1999)", vm.Subtitle);
    }

    [AvaloniaFact]
    public void A_track_change_re_raises_the_track_subtitle_and_total_time()
    {
        var track = T("A");
        var vm = Make(out _, out var playback, track);
        var raised = new List<string?>();
        vm.PropertyChanged += (_, e) => raised.Add(e.PropertyName);

        playback.Play(track);

        Assert.Contains(nameof(vm.CurrentlyPlayingTrack), raised);
        Assert.Contains(nameof(vm.Subtitle), raised);
        Assert.Contains(nameof(vm.TotalTime), raised);
    }

    [Fact]
    public void ElapsedTime_is_null_before_playback_starts_and_formatted_after()
    {
        var vm = Make(out var audio, out _);

        Assert.Null(vm.ElapsedTime);

        audio.Time = (long)TimeSpan.FromSeconds(75).TotalMilliseconds;
        Assert.Equal("1:15", vm.ElapsedTime);

        audio.Time = (long)TimeSpan.FromMinutes(61).TotalMilliseconds;
        Assert.Equal("1:01:00", vm.ElapsedTime);
    }

    // The track's own tagged duration wins over the audio manager's, which is
    // only known once the file has actually been opened.
    [AvaloniaFact]
    public void TotalTime_prefers_the_tracks_duration_over_the_audio_managers()
    {
        var track = T("A");
        var vm = Make(out var audio, out var playback, track);
        audio.Length = (long)TimeSpan.FromSeconds(999).TotalMilliseconds;

        playback.Play(track);

        Assert.Equal("3:35", vm.TotalTime); // 215s, not 999s
    }

    [AvaloniaFact]
    public void TotalTime_falls_back_to_the_audio_manager_for_an_untagged_duration()
    {
        var track = new Track { Title = "A", Path = "/music/A.mp3", Duration = TimeSpan.Zero };
        var vm = Make(out var audio, out var playback, track);
        audio.Length = (long)TimeSpan.FromSeconds(90).TotalMilliseconds;

        playback.Play(track);

        Assert.Equal("1:30", vm.TotalTime);
    }

    [Fact]
    public void TotalTime_is_null_when_neither_source_knows_a_duration()
    {
        var vm = Make(out _, out _);

        Assert.Null(vm.TotalTime);
    }

    // The seek debounce exists because firing a native seek per pointer-move
    // tick wedged the decode pipeline. Nothing may reach the audio manager
    // until the drag pauses.
    [Fact]
    public void Dragging_the_seek_bar_does_not_seek_immediately()
    {
        var vm = Make(out var audio, out _);
        audio.IsPlaying = true;
        audio.Position = 0;

        vm.SeekPosition = 0.1;
        vm.SeekPosition = 0.2;
        vm.SeekPosition = 0.3;

        Assert.Equal(0, audio.Position);
        Assert.Equal(0.3, vm.SeekPosition); // the slider itself still tracks the drag
    }

    [Fact]
    public void Only_the_last_position_after_the_drag_settles_is_seeked_to()
    {
        var vm = Make(out var audio, out _);
        audio.IsPlaying = true;

        vm.SeekPosition = 0.1;
        vm.SeekPosition = 0.9;

        Assert.True(SpinWait.SpinUntil(() => audio.Position > 0, TimeSpan.FromSeconds(5)),
                    "the debounced seek never reached the audio manager");
        Assert.Equal(0.9f, audio.Position, 3);
    }

    // Nothing is playing, so there is nothing to seek in - a slider reset must
    // not turn into a seek.
    [Fact]
    public void Setting_SeekPosition_while_stopped_never_seeks()
    {
        var vm = Make(out var audio, out _);
        audio.IsPlaying = false;

        vm.SeekPosition = 0.5;

        Thread.Sleep(300); // longer than the 150ms debounce
        Assert.Equal(0, audio.Position);
    }

    // Position updates coming *from* the audio manager drive the slider, and
    // must not bounce straight back out as a seek.
    [AvaloniaFact]
    public void A_position_update_from_the_audio_manager_moves_the_slider_without_seeking_back()
    {
        var vm = Make(out var audio, out _);
        audio.IsPlaying = true;
        audio.Position = 0.42f;

        audio.RaisePositionChanged();
        Dispatcher.UIThread.RunJobs();

        Assert.Equal(0.42, vm.SeekPosition, 3);

        // No debounced seek was armed by that. Playback moving on afterwards
        // must therefore stand: without the _isUpdatingFromAudio guard the
        // debounce fires here and stomps the position back to 0.42.
        audio.Position = 0.55f;
        Thread.Sleep(400);
        Assert.Equal(0.55f, audio.Position, 3);
    }

    [AvaloniaFact]
    public void Stopping_resets_the_slider_to_zero()
    {
        var vm = Make(out var audio, out _);
        audio.IsPlaying = true;
        audio.Position = 0.8f;
        audio.RaisePositionChanged();
        Dispatcher.UIThread.RunJobs();

        audio.IsPlaying = false;
        audio.RaiseStopped();
        Dispatcher.UIThread.RunJobs();

        Assert.Equal(0, vm.SeekPosition);
    }

    [AvaloniaFact]
    public void Repeat_and_shuffle_are_forwarded_to_the_playback_view_model_and_re_raised()
    {
        var vm = Make(out _, out var playback);
        var raised = new List<string?>();
        vm.PropertyChanged += (_, e) => raised.Add(e.PropertyName);

        vm.ToggleRepeat();
        vm.ToggleShuffle();

        Assert.Equal(playback.IsRepeatEnabled, vm.IsRepeatEnabled);
        Assert.Equal(playback.IsShuffleEnabled, vm.IsShuffleEnabled);
        Assert.True(vm.IsRepeatEnabled);
        Assert.True(vm.IsShuffleEnabled);
        Assert.Contains(nameof(vm.IsRepeatEnabled), raised);
        Assert.Contains(nameof(vm.IsShuffleEnabled), raised);
    }

    // A live peer-stream URL is not a filesystem path - reading it as one threw
    // TagLib/IO exceptions per track change.
    // A live peer-stream URL is skipped straight to no-art rather than being
    // handed to TagLib as a filesystem path. Both routes end at AlbumArt=null,
    // so the difference is only visible in whether a read was attempted at all
    // - which is the entire cost being avoided, once per track change.
    [AvaloniaFact]
    public void A_peer_stream_url_is_never_read_as_a_file_path()
    {
        var streamed = new Track { Title = "Remote", Path = "http://192.168.1.5:5001/stream/7" };
        var audio = new FakeAudioManager();
        var library = new Library(new List<Track> { streamed });
        var playback = new PlaylistControlViewModel(
            audio, new MainPlaylist(new List<Track> { streamed }), library, new AppSettings(),
            new LibraryStore(NullLogger<LibraryStore>.Instance),
            new AppSettingsStore(NullLogger<AppSettingsStore>.Instance),
            NullLogger<PlaylistControlViewModel>.Instance);
        var logger = new RecordingLogger<CurrentlyPlayingControlViewModel>();
        var vm = new CurrentlyPlayingControlViewModel(playback, audio, library, logger);

        playback.Play(streamed);
        Thread.Sleep(200); // the art load, if attempted, runs on a Task.Run
        Dispatcher.UIThread.RunJobs();

        Assert.Null(vm.AlbumArt);
        Assert.DoesNotContain(logger.Messages, m => m.Contains("Could not read/decode"));
    }

    // Records what was logged so a test can assert a code path was never taken.
    private sealed class RecordingLogger<T> : Microsoft.Extensions.Logging.ILogger<T>
    {
        public List<string> Messages { get; } = new();

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(Microsoft.Extensions.Logging.LogLevel logLevel) => true;

        public void Log<TState>(Microsoft.Extensions.Logging.LogLevel logLevel,
                                Microsoft.Extensions.Logging.EventId eventId,
                                TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            lock (Messages)
                Messages.Add(formatter(state, exception));
        }
    }
}
