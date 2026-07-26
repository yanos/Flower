using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;

using Avalonia.Headless.XUnit;
using Avalonia.Threading;

using Microsoft.Extensions.Logging.Abstractions;

using Flower.Manager;
using Flower.Models;
using Flower.Persistence;
using Flower.Tests.TestSupport;
using Flower.ViewModels;

namespace Flower.Tests;

// Full pipeline, real orchestration end to end: PlaylistControlViewModel +
// CurrentlyPlayingControlViewModel -> GaplessAudioManager -> GaplessCoordinator
// -> ITrackDecoder -> IAudioSink. Uses FakeTrackDecoder (not real LibVLC
// decode) rather than LibVlcFixture - GaplessCoordinatorRealDecodeTests
// already proved real PCM splices cleanly at the coordinator level when it
// isn't hitting the real-decode concurrency race tracked separately (see
// its Skip comment); this layer's job is to prove the ORCHESTRATION above
// that - transitions, next/previous, pause/resume, scrubbing - is wired
// correctly through the real classes, which doesn't need real decode to
// verify and is unaffected by that race.
//
// PlatformDataDirectory.Current is pinned (same reasoning/pattern as
// PlaylistAutoAdvanceTests) since natural EndReached handling calls
// LibraryStore.SaveAsync.
[Collection("PlatformDataDirectory")]
public class PlaylistPlaybackIntegrationTests : IDisposable
{
    private readonly string _tempHome;

    public PlaylistPlaybackIntegrationTests()
    {
        _tempHome = Path.Combine(Path.GetTempPath(), "flower-tests-" + Guid.NewGuid());
        Directory.CreateDirectory(_tempHome);
        PlatformDataDirectory.Current = _tempHome;
    }

    public void Dispose()
    {
        PlatformDataDirectory.Current = null;
        try { Directory.Delete(_tempHome, recursive: true); } catch { /* best effort */ }
    }

    private static Track T(string title, TimeSpan? duration = null) =>
        new() { Title = title, Path = $"/music/{title}.mp3", Duration = duration ?? TimeSpan.FromMinutes(3) };

    private sealed class Harness
    {
        public PlaylistControlViewModel PlaylistControl { get; }
        public CurrentlyPlayingControlViewModel CurrentlyPlaying { get; }
        public FakeAudioSink Sink { get; } = new();

        // A track goes through more than one decoder instance when it's
        // replayed/re-armed (e.g. repeat), so this has to track every
        // instance ever created for it, not just the latest.
        private readonly Dictionary<Track, List<FakeTrackDecoder>> _decoders = [];

        public Harness(List<Track> tracks)
        {
            var ring = new GaplessRingBuffer(4096);
            var coordinator = new GaplessCoordinator(ring, (track, r) =>
            {
                var fake = new FakeTrackDecoder(track);
                if (!_decoders.TryGetValue(track, out var list))
                    _decoders[track] = list = [];
                list.Add(fake);
                return fake;
            });
            var audioManager = new GaplessAudioManager(ring, coordinator, Sink, NullLogger<GaplessAudioManager>.Instance);

            var library = new Library(tracks);
            var playlist = new MainPlaylist(tracks);
            var libraryStore = new LibraryStore(NullLogger<LibraryStore>.Instance);
            var appSettingsStore = new AppSettingsStore(NullLogger<AppSettingsStore>.Instance);

            PlaylistControl = new PlaylistControlViewModel(
                audioManager, playlist, library, new AppSettings(), libraryStore, appSettingsStore,
                NullLogger<PlaylistControlViewModel>.Instance);
            CurrentlyPlaying = new CurrentlyPlayingControlViewModel(
                PlaylistControl, audioManager, library, NullLogger<CurrentlyPlayingControlViewModel>.Instance);
        }

        public IReadOnlyList<FakeTrackDecoder> DecodersFor(Track track) =>
            _decoders.TryGetValue(track, out var list) ? list : [];

        public FakeTrackDecoder LatestDecoderFor(Track track)
        {
            var list = DecodersFor(track);
            Assert.NotEmpty(list);
            return list[^1];
        }
    }

    private static void PumpUntil(Func<bool> condition, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (!condition())
        {
            Dispatcher.UIThread.RunJobs();
            if (DateTime.UtcNow > deadline)
            {
                Assert.Fail("condition was not met within the timeout");
                return;
            }
            Thread.Sleep(1);
        }
    }

    [AvaloniaFact]
    public void Natural_playback_transitions_through_the_playlist_in_order()
    {
        var a = T("A");
        var b = T("B");
        var c = T("C");
        var h = new Harness([a, b, c]);

        h.PlaylistControl.Play(a);
        PumpUntil(() => h.LatestDecoderFor(a).StartDecodingCalled, TimeSpan.FromSeconds(5));

        h.LatestDecoderFor(a).RaiseDrained();
        PumpUntil(() => h.PlaylistControl.CurrentlyPlayingTrack == b, TimeSpan.FromSeconds(5));

        h.LatestDecoderFor(b).RaiseDrained();
        PumpUntil(() => h.PlaylistControl.CurrentlyPlayingTrack == c, TimeSpan.FromSeconds(5));
    }

    [AvaloniaFact]
    public void Next_and_Previous_navigate_through_the_real_audio_manager()
    {
        var a = T("A");
        var b = T("B");
        var h = new Harness([a, b]);

        h.PlaylistControl.Play(a);
        PumpUntil(() => h.LatestDecoderFor(a).StartDecodingCalled, TimeSpan.FromSeconds(5));

        h.PlaylistControl.Next();
        Assert.Same(b, h.PlaylistControl.CurrentlyPlayingTrack);

        h.PlaylistControl.Previous();
        Assert.Same(a, h.PlaylistControl.CurrentlyPlayingTrack);

        // Palylist.GetPreviousTrack's documented no-wrap-at-start behavior.
        h.PlaylistControl.Previous();
        Assert.Same(a, h.PlaylistControl.CurrentlyPlayingTrack);
    }

    [AvaloniaFact]
    public void PlayOrPause_toggles_playback_through_the_real_sink()
    {
        var a = T("A");
        var h = new Harness([a]);

        h.PlaylistControl.Play(a);
        Assert.True(h.PlaylistControl.IsPlaying);
        Assert.True(h.Sink.IsPlaying);

        h.PlaylistControl.PlayOrPause();
        Assert.False(h.PlaylistControl.IsPlaying);
        Assert.False(h.Sink.IsPlaying);

        h.PlaylistControl.PlayOrPause();
        Assert.True(h.PlaylistControl.IsPlaying);
        Assert.True(h.Sink.IsPlaying);
    }

    [AvaloniaFact]
    public void Scrubbing_seeks_the_current_decoder_after_the_real_debounce()
    {
        // Two tracks, not one - a single-track playlist wraps to itself as
        // its own upcoming track (Palylist.GetNextTrack falls back to
        // FirstOrDefault), which would arm a *second* decoder instance for
        // "A" that never gets seeked, and LatestDecoderFor(a) would then
        // point at that one instead of the one actually playing.
        var a = T("A");
        var b = T("B");
        var h = new Harness([a, b]);

        h.PlaylistControl.Play(a);
        PumpUntil(() => h.LatestDecoderFor(a).StartDecodingCalled, TimeSpan.FromSeconds(5));

        h.CurrentlyPlaying.SeekPosition = 0.4;

        // CurrentlyPlayingControlViewModel debounces scrubbing 150ms before
        // actually applying it - has to be let through for real, not
        // pumped past, since it's a real System.Timers.Timer.
        PumpUntil(() => h.LatestDecoderFor(a).LastSeekPosition != null, TimeSpan.FromSeconds(5));

        Assert.Equal(0.4f, h.LatestDecoderFor(a).LastSeekPosition);
    }

    [AvaloniaFact]
    public void EndReached_with_repeat_enabled_replays_the_same_track_through_the_real_coordinator()
    {
        var a = T("A");
        var b = T("B");
        var h = new Harness([a, b]);
        h.PlaylistControl.ToggleRepeat();

        h.PlaylistControl.Play(a);

        // Repeat re-arms the same track immediately (Play() calls
        // SetUpcoming right after), so by now DecodersFor(a) already has a
        // second, armed instance alongside the first, current one - grab
        // the original specifically rather than "whichever's latest".
        PumpUntil(() => h.DecodersFor(a).Count >= 2, TimeSpan.FromSeconds(5));
        var firstInstance = h.DecodersFor(a)[0];
        var secondInstance = h.DecodersFor(a)[1];
        PumpUntil(() => secondInstance.StartDecodingCalled, TimeSpan.FromSeconds(5));

        firstInstance.RaiseDrained();

        // Promotes the second FakeTrackDecoder instance for "A" through the
        // real GaplessCoordinator handover machinery - not just a
        // ViewModel-level decision (already covered by
        // PlaylistAutoAdvanceTests). RetireCalled on the first instance is
        // GaplessCoordinator's own signal that it actually processed the
        // drain and promoted the second instance, rather than something
        // that was already true beforehand.
        PumpUntil(() => firstInstance.RetireCalled, TimeSpan.FromSeconds(5));
        Assert.Same(a, h.PlaylistControl.CurrentlyPlayingTrack);
        Assert.False(secondInstance.RetireCalled);
    }
}
