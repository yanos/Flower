using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

using Avalonia.Headless.XUnit;
using Avalonia.Threading;

using Microsoft.Extensions.Logging.Abstractions;

using Flower.Manager;
using Flower.Models;
using Flower.Persistence;
using Flower.Services;
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
        PlatformDataDirectory.Current = AssemblySetup.DefaultDataDirectory;
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
        //
        // Reference equality, not Track's own record value-equality: Play()
        // routes through the real PlaylistControlViewModel -> Library.
        // RecordPlayed, which stamps LastPlayedAt on this exact track
        // instance in place. That mutation happens after the instance is
        // already a dictionary key here, and since Track's generated
        // GetHashCode/Equals fold in every property including LastPlayedAt,
        // the key's hash changes underneath the dictionary and later
        // lookups land in the wrong bucket - surfacing as LatestDecoderFor
        // asserting an empty collection for a track that was, in fact,
        // already decoding.
        private readonly Dictionary<Track, List<FakeTrackDecoder>> _decoders = new(ReferenceEqualityComparer.Instance);

        // Every track actually handed to a decoder, in order. Needed alongside
        // the per-instance dictionary above because a placeholder never reaches
        // the decoder as itself - it arrives as the transient stream-URL copy
        // (see PlaylistControlViewModel.ResolveForPlayback), which is a
        // different instance and so invisible to a reference-keyed lookup.
        public List<Track> DecodedTracks { get; } = [];

        public Harness(List<Track> tracks, IStreamUrlResolver? streamUrlResolver = null)
        {
            var ring = new GaplessRingBuffer(4096);
            var coordinator = new GaplessCoordinator(ring, (track, r) =>
            {
                var fake = new FakeTrackDecoder(track);
                DecodedTracks.Add(track);
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
                audioManager, playlist, library, new AppSettings(), appSettingsStore,
                NullLogger<PlaylistControlViewModel>.Instance, streamUrlResolver);
            CurrentlyPlaying = new CurrentlyPlayingControlViewModel(
                PlaylistControl, audioManager, library, new AlbumArtLoader(null, null, NullLogger<AlbumArtLoader>.Instance), NullLogger<CurrentlyPlayingControlViewModel>.Instance);
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

    // ── Local vs. remote: the same pipeline, both sources ────────────────
    //
    // A library can hold two kinds of track: a local file (Path is a real path)
    // and a placeholder synced in from a peer's catalog that this device has
    // never downloaded (Path is null, OriginTrackId names it on that peer - see
    // SYNC-PLAN.md Phase 3). Only the first can go to a decoder as-is; the
    // second has to become a stream URL first.
    //
    // That resolution used to live above this class, in
    // MainViewModel.PlayResolvingPlaceholder, so only callers that went through
    // MainViewModel got it. Everything inside PlaylistControlViewModel - auto-
    // advance, skip-on-failure, Next, Previous, decode-ahead - handed the raw
    // placeholder straight to the decoder, which throws in
    // TrackDecoder.EnsureMedia. Manual play worked and auto-advance did not.
    // These tests run both kinds through the real pipeline, and specifically
    // pin the entry points that used to skip resolution.

    private static Track P(string title) => new()
    {
        Title = title,
        Path = null,
        OriginTrackId = "sg-" + title,
        OriginDeviceFingerprint = "peer-fingerprint",
        Duration = TimeSpan.FromMinutes(3),
    };

    private static string StreamUrlFor(Track track) =>
        $"http://server.local:53317/rest/stream?id={track.OriginTrackId}";

    private sealed class FakeStreamUrlResolver(bool reachable = true) : IStreamUrlResolver
    {
        // Every track this was asked about - lets a test assert that a local
        // file never goes near the peer resolution path at all.
        public List<Track> Asked { get; } = [];

        // Held-open answers, one per track asked about, for the tests that
        // exercise the browser's shape: a URL that is a network round trip away
        // rather than in hand. Left empty, every answer completes immediately
        // and playback starts on the calling stack, which is what every other
        // head does and what the rest of this file assumes.
        public bool Defer { get; init; }

        public List<TaskCompletionSource<string?>> Pending { get; } = [];

        public Task<string?> ResolveAsync(Track track)
        {
            Asked.Add(track);
            var url = reachable ? StreamUrlFor(track) : null;
            if (!Defer)
                return Task.FromResult(url);

            var pending = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
            Pending.Add(pending);
            return pending.Task;
        }
    }

    [AvaloniaFact]
    public void A_local_track_plays_straight_from_its_own_path()
    {
        var a = T("A");
        var resolver = new FakeStreamUrlResolver();
        var h = new Harness([a], resolver);

        h.PlaylistControl.Play(a);
        PumpUntil(() => h.LatestDecoderFor(a).StartDecodingCalled, TimeSpan.FromSeconds(5));

        // The very same instance, unmodified: a local track is not copied, not
        // rewritten, and never asks a peer for anything.
        Assert.Same(a, h.PlaylistControl.CurrentlyPlayingTrack);
        Assert.Equal("/music/A.mp3", h.LatestDecoderFor(a).Track.Path);
        Assert.Empty(resolver.Asked);
    }

    [AvaloniaFact]
    public void A_remote_placeholder_plays_from_a_stream_url_without_being_downloaded()
    {
        var r = P("R");
        var h = new Harness([r], new FakeStreamUrlResolver());

        h.PlaylistControl.Play(r);
        PumpUntil(() => h.DecodedTracks.Count > 0, TimeSpan.FromSeconds(5));

        Assert.Contains(h.DecodedTracks, t => t.Id == r.Id && t.Path == StreamUrlFor(r));

        // The placeholder itself is still a placeholder. It lives in
        // Library.Tracks, and a stream URL must never be persisted there - only
        // the transient copy carries one.
        Assert.Null(r.Path);
    }

    [AvaloniaFact]
    public void Auto_advance_onto_a_remote_placeholder_streams_it()
    {
        var a = T("A");
        var r = P("R");
        var h = new Harness([a, r], new FakeStreamUrlResolver());

        h.PlaylistControl.Play(a);
        PumpUntil(() => h.LatestDecoderFor(a).StartDecodingCalled, TimeSpan.FromSeconds(5));

        h.LatestDecoderFor(a).RaiseDrained();
        PumpUntil(() => h.PlaylistControl.CurrentlyPlayingTrack?.Id == r.Id, TimeSpan.FromSeconds(5));

        Assert.Contains(h.DecodedTracks, t => t.Id == r.Id && t.Path == StreamUrlFor(r));
    }

    [AvaloniaFact]
    public void Skipping_a_failed_track_onto_a_remote_placeholder_streams_it()
    {
        // The exact reported crash: a track failed to decode, the skip-on-
        // failure handler advanced to the next one, and that one was an
        // undownloaded placeholder handed straight to the decoder.
        var a = T("A");
        var r = P("R");
        var h = new Harness([a, r], new FakeStreamUrlResolver());

        h.PlaylistControl.Play(a);
        PumpUntil(() => h.LatestDecoderFor(a).StartDecodingCalled, TimeSpan.FromSeconds(5));

        h.LatestDecoderFor(a).RaiseFaulted();
        PumpUntil(() => h.PlaylistControl.CurrentlyPlayingTrack?.Id == r.Id, TimeSpan.FromSeconds(5));

        Assert.Contains(h.DecodedTracks, t => t.Id == r.Id && t.Path == StreamUrlFor(r));
    }

    [AvaloniaFact]
    public void Decode_ahead_arms_the_streamed_copy_of_an_upcoming_placeholder()
    {
        // SetUpcoming is the other way a track reaches a decoder, and it never
        // went through the old resolution either - so arming decode-ahead on an
        // undownloaded next track failed on its own, before playback got there.
        var a = T("A");
        var r = P("R");
        var h = new Harness([a, r], new FakeStreamUrlResolver());

        h.PlaylistControl.Play(a);
        PumpUntil(() => h.DecodedTracks.Exists(t => t.Id == r.Id), TimeSpan.FromSeconds(5));

        Assert.Contains(h.DecodedTracks, t => t.Id == r.Id && t.Path == StreamUrlFor(r));
    }

    [AvaloniaFact]
    public void A_remote_placeholder_with_no_reachable_peer_is_not_played()
    {
        // Nothing to stream from is an ordinary outcome, not a crash: the peer
        // holding this track may simply be off right now.
        var r = P("R");
        var h = new Harness([r], new FakeStreamUrlResolver(reachable: false));

        h.PlaylistControl.Play(r);
        Dispatcher.UIThread.RunJobs();

        Assert.Null(h.PlaylistControl.CurrentlyPlayingTrack);
        Assert.Empty(h.DecodedTracks);
    }

    // ── When the URL is a round trip away ────────────────────────────────
    //
    // The browser cannot answer "what URL plays this track?" without asking its
    // server to mint a ticket for that exact track (see StreamTicketUrlResolver).
    // Play() therefore has to cope with a URL that is not in hand yet, without
    // making every other head - which resolves synchronously and is asserted on
    // the very next line all over this file - pay for it.

    [AvaloniaFact]
    public void A_track_whose_url_is_a_round_trip_away_starts_when_it_lands()
    {
        var r = P("R");
        var resolver = new FakeStreamUrlResolver { Defer = true };
        var h = new Harness([r], resolver);

        h.PlaylistControl.Play(r);
        Dispatcher.UIThread.RunJobs();

        // Nothing has started: the URL is still in flight.
        Assert.Null(h.PlaylistControl.CurrentlyPlayingTrack);

        resolver.Pending[0].SetResult(StreamUrlFor(r));
        PumpUntil(() => h.PlaylistControl.CurrentlyPlayingTrack != null, TimeSpan.FromSeconds(2));

        Assert.Equal(r.Id, h.PlaylistControl.CurrentlyPlayingTrack!.Id);
        Assert.Equal(StreamUrlFor(r), h.PlaylistControl.CurrentlyPlayingTrack.Path);
    }

    [AvaloniaFact]
    public void A_url_that_arrives_after_another_track_was_started_is_discarded()
    {
        // The failure this exists to stop: press play on one track, get bored
        // and play another, and have the first one hijack playback a second
        // later when its URL finally arrives. Whatever was asked for last wins.
        var first = P("First");
        var second = P("Second");
        var resolver = new FakeStreamUrlResolver { Defer = true };
        var h = new Harness([first, second], resolver);

        h.PlaylistControl.Play(first);
        h.PlaylistControl.Play(second);
        Dispatcher.UIThread.RunJobs();

        // Answered out of order on purpose - the guard is the generation, not
        // the arrival order.
        resolver.Pending[0].SetResult(StreamUrlFor(first));
        resolver.Pending[1].SetResult(StreamUrlFor(second));
        PumpUntil(() => h.PlaylistControl.CurrentlyPlayingTrack != null, TimeSpan.FromSeconds(2));
        Dispatcher.UIThread.RunJobs();

        Assert.Equal(second.Id, h.PlaylistControl.CurrentlyPlayingTrack!.Id);
        Assert.DoesNotContain(h.DecodedTracks, t => t.Id == first.Id);
    }

    [AvaloniaFact]
    public void A_round_trip_that_comes_back_with_nothing_plays_nothing()
    {
        // The deferred spelling of "no stream URL could be built" - a refused
        // ticket has to be as harmless as an unreachable peer.
        var r = P("R");
        var resolver = new FakeStreamUrlResolver { Defer = true };
        var h = new Harness([r], resolver);

        h.PlaylistControl.Play(r);
        resolver.Pending[0].SetResult(null);
        Dispatcher.UIThread.RunJobs();
        Thread.Sleep(20);
        Dispatcher.UIThread.RunJobs();

        Assert.Null(h.PlaylistControl.CurrentlyPlayingTrack);
        Assert.Empty(h.DecodedTracks);
    }

    [AvaloniaFact]
    public void A_local_track_never_waits_on_anything()
    {
        // The property that keeps this change free everywhere but the browser:
        // a track with a real path is not asked about at all, so it starts on
        // the calling stack with no dispatcher turn in between.
        var a = T("A");
        var resolver = new FakeStreamUrlResolver { Defer = true };
        var h = new Harness([a], resolver);

        h.PlaylistControl.Play(a);

        Assert.Equal(a.Id, h.PlaylistControl.CurrentlyPlayingTrack!.Id);
        Assert.Empty(resolver.Pending);
    }

    [AvaloniaFact]
    public void A_streaming_placeholder_keeps_its_place_in_the_queue()
    {
        // The transient copy keeps Track.Id, so the queue still recognizes it
        // (see Track.Clone). Without that, navigation off a streaming track
        // falls back to the front of the queue rather than moving one on.
        var r = P("R");
        var b = T("B");
        var h = new Harness([r, b], new FakeStreamUrlResolver());

        h.PlaylistControl.Play(r);
        PumpUntil(() => h.DecodedTracks.Exists(t => t.Id == r.Id), TimeSpan.FromSeconds(5));

        h.PlaylistControl.Next();
        Assert.Same(b, h.PlaylistControl.CurrentlyPlayingTrack);
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

        // the documented no-wrap-at-start behavior of Previous().
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
        // its own upcoming track (queue navigation falls back to
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
