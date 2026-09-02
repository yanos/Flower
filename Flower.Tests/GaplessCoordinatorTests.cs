using System;
using System.Collections.Generic;
using System.Threading;
using Flower.Audio;
using Flower.Models;
using Flower.Tests.TestSupport;

namespace Flower.Tests;

// Exercises GaplessCoordinator's handover/idempotency/generation state
// machine against a fake ITrackDecoder, so these tests never touch real
// LibVLC decode.
public class GaplessCoordinatorTests
{
    private sealed class Harness
    {
        public GaplessCoordinator Coordinator { get; }
        public GaplessRingBuffer SharedRing { get; }
        public RecordingLogger<GaplessCoordinator>? Logger { get; }

        private readonly Dictionary<Track, List<FakeTrackDecoder>> _decoders = [];
        private readonly HashSet<Track> _failToPrepare = [];

        public Harness(bool captureLogs = false, int sharedRingCapacity = 1024)
        {
            SharedRing = new GaplessRingBuffer(sharedRingCapacity);
            Logger = captureLogs ? new RecordingLogger<GaplessCoordinator>() : null;
            Coordinator = new GaplessCoordinator(SharedRing, (track, ring) =>
            {
                var fake = new FakeTrackDecoder(track) { PrepareResult = !_failToPrepare.Contains(track) };
                if (!_decoders.TryGetValue(track, out var list))
                    _decoders[track] = list = [];
                list.Add(fake);
                return fake;
            }, Logger);
        }

        public void FailToPrepare(Track track) => _failToPrepare.Add(track);

        // Every decoder ever created for this track, in creation order - a
        // track goes through more than one when it's replayed/re-armed
        // after being retired.
        public IReadOnlyList<FakeTrackDecoder> DecodersFor(Track track) =>
            _decoders.TryGetValue(track, out var list) ? list : [];

        public FakeTrackDecoder LatestDecoderFor(Track track)
        {
            var list = DecodersFor(track);
            Assert.NotEmpty(list);
            return list[^1];
        }
    }

    private static Track T(string title) => new() { Title = title, Path = $"/music/{title}.mp3", Duration = TimeSpan.FromMinutes(3) };

    private static void WaitUntil(Func<bool> condition, string because)
    {
        Assert.True(SpinWait.SpinUntil(condition, TimeSpan.FromSeconds(5)), because);
    }

    [Fact]
    public void Play_starts_decoding_the_given_track()
    {
        var h = new Harness();
        var a = T("A");

        h.Coordinator.Play(a);

        WaitUntil(() => h.LatestDecoderFor(a).StartDecodingCalled, "Play should start decoding immediately");
    }

    [Fact]
    public void Diagnostic_snapshot_warns_when_rendering_consumes_no_pcm_between_snapshots()
    {
        var h = new Harness(captureLogs: true);
        var a = T("A");
        h.Coordinator.Play(a);
        WaitUntil(() => h.LatestDecoderFor(a).StartDecodingCalled, "A should start");

        h.Coordinator.LogDiagnosticSnapshot(renderStarted: true);
        h.Coordinator.LogDiagnosticSnapshot(renderStarted: true);

        Assert.Equal(1, h.Logger!.CountAt(
            Microsoft.Extensions.Logging.LogLevel.Warning,
            "made no PCM consumption progress"));
    }

    [Fact]
    public void Completion_log_warns_when_unplayed_pcm_has_no_armed_successor()
    {
        var h = new Harness(captureLogs: true, sharedRingCapacity: 48_000);
        var a = T("A");
        h.Coordinator.Play(a);
        WaitUntil(() => h.LatestDecoderFor(a).StartDecodingCalled, "A should start");
        h.LatestDecoderFor(a).BytesProduced = 3 * 60 * (long)GaplessFormat.SampleRate * GaplessFormat.BytesPerFrame;
        h.SharedRing.TryWrite(new byte[24_000]);

        h.LatestDecoderFor(a).RaiseDrained();

        Assert.Equal(1, h.Logger!.CountAt(
            Microsoft.Extensions.Logging.LogLevel.Warning,
            "no armed successor"));
    }

    [Fact]
    public void Play_with_a_different_track_hard_flushes_the_old_decoder()
    {
        var h = new Harness();
        var a = T("A");
        var b = T("B");
        h.Coordinator.Play(a);
        WaitUntil(() => h.LatestDecoderFor(a).StartDecodingCalled, "A should start");

        h.Coordinator.Play(b);

        Assert.True(h.LatestDecoderFor(a).RetireCalled);
        WaitUntil(() => h.LatestDecoderFor(b).StartDecodingCalled, "B should start");
    }

    [Fact]
    public void Play_is_a_no_op_for_the_track_that_just_became_current_via_a_natural_handover()
    {
        var h = new Harness();
        var a = T("A");
        var b = T("B");
        h.Coordinator.Play(a);
        WaitUntil(() => h.LatestDecoderFor(a).StartDecodingCalled, "A should start");
        h.Coordinator.SetUpcoming(b);
        WaitUntil(() => h.LatestDecoderFor(b).StartDecodingCalled, "B should be armed and start decode-ahead");

        h.LatestDecoderFor(a).RaiseDrained();
        Assert.Same(b, h.Coordinator.CurrentTrack);
        var promotedB = h.LatestDecoderFor(b);

        // PlaylistControlViewModel's EndReached handler always calls Play()
        // again with whatever it computed as next - here that's exactly the
        // track that already became current via the handover above.
        h.Coordinator.Play(b);

        Assert.Single(h.DecodersFor(b));
        Assert.False(promotedB.RetireCalled);
    }

    [Fact]
    public void SetUpcoming_null_clears_the_armed_slot()
    {
        var h = new Harness();
        var a = T("A");
        var b = T("B");
        h.Coordinator.Play(a);
        WaitUntil(() => h.LatestDecoderFor(a).StartDecodingCalled, "A should start");
        h.Coordinator.SetUpcoming(b);
        WaitUntil(() => h.LatestDecoderFor(b).StartDecodingCalled, "B should be armed");

        h.Coordinator.SetUpcoming(null);

        Assert.True(h.LatestDecoderFor(b).RetireCalled);
    }

    [Fact]
    public void SetUpcoming_with_the_track_already_armed_is_a_no_op()
    {
        var h = new Harness();
        var a = T("A");
        var b = T("B");
        h.Coordinator.Play(a);
        WaitUntil(() => h.LatestDecoderFor(a).StartDecodingCalled, "A should start");

        h.Coordinator.SetUpcoming(b);
        WaitUntil(() => h.LatestDecoderFor(b).StartDecodingCalled, "B should be armed");
        h.Coordinator.SetUpcoming(b);

        Assert.Single(h.DecodersFor(b));
    }

    [Fact]
    public void Drained_with_nothing_armed_fires_EndReached_and_clears_current()
    {
        var h = new Harness();
        var a = T("A");
        Track? endReachedTrack = null;
        h.Coordinator.EndReached += t => endReachedTrack = t;
        h.Coordinator.Play(a);
        WaitUntil(() => h.LatestDecoderFor(a).StartDecodingCalled, "A should start");

        h.LatestDecoderFor(a).RaiseDrained();

        Assert.Same(a, endReachedTrack);
        Assert.Null(h.Coordinator.CurrentTrack);
    }

    [Fact]
    public void Drained_with_an_armed_track_promotes_it_into_the_shared_ring_and_still_fires_EndReached()
    {
        var h = new Harness();
        var a = T("A");
        var b = T("B");
        Track? endReachedTrack = null;
        h.Coordinator.EndReached += t => endReachedTrack = t;
        h.Coordinator.Play(a);
        WaitUntil(() => h.LatestDecoderFor(a).StartDecodingCalled, "A should start");
        h.Coordinator.SetUpcoming(b);
        WaitUntil(() => h.LatestDecoderFor(b).StartDecodingCalled, "B should be armed");

        h.LatestDecoderFor(a).RaiseDrained();

        Assert.Same(a, endReachedTrack);
        Assert.Same(b, h.Coordinator.CurrentTrack);
        Assert.Same(h.SharedRing, h.LatestDecoderFor(b).PromotedTo);
        Assert.True(h.LatestDecoderFor(a).RetireCalled);
    }

    // A faulted current track advances the pipeline exactly like a drained one
    // (promote the armed track, or stop) - but reports itself as TrackFailed,
    // not EndReached. Both used to raise EndReached, which is what
    // PlaylistControlViewModel counts a play on, so an unplayable file was
    // indistinguishable from one the user listened all the way through.
    [Fact]
    public void Faulted_current_track_advances_like_Drained_but_reports_TrackFailed()
    {
        var h = new Harness();
        var a = T("A");
        Track? endReachedTrack = null;
        Track? failedTrack = null;
        h.Coordinator.EndReached += t => endReachedTrack = t;
        h.Coordinator.TrackFailed += t => failedTrack = t;
        h.Coordinator.Play(a);
        WaitUntil(() => h.LatestDecoderFor(a).StartDecodingCalled, "A should start");

        h.LatestDecoderFor(a).RaiseFaulted();

        Assert.Same(a, failedTrack);
        Assert.Null(endReachedTrack);
        Assert.Null(h.Coordinator.CurrentTrack);
    }

    [Fact]
    public void Faulted_current_track_still_promotes_the_armed_track()
    {
        var h = new Harness();
        var a = T("A");
        var b = T("B");
        h.Coordinator.Play(a);
        WaitUntil(() => h.LatestDecoderFor(a).StartDecodingCalled, "A should start");
        h.Coordinator.SetUpcoming(b);
        WaitUntil(() => h.LatestDecoderFor(b).StartDecodingCalled, "B should be armed");

        h.LatestDecoderFor(a).RaiseFaulted();

        Assert.Same(b, h.Coordinator.CurrentTrack);
    }

    [Fact]
    public void Faulted_armed_track_clears_it_without_promoting()
    {
        var h = new Harness();
        var a = T("A");
        var b = T("B");
        Track? endReachedTrack = null;
        h.Coordinator.EndReached += t => endReachedTrack = t;
        h.Coordinator.Play(a);
        WaitUntil(() => h.LatestDecoderFor(a).StartDecodingCalled, "A should start");
        h.Coordinator.SetUpcoming(b);
        WaitUntil(() => h.LatestDecoderFor(b).StartDecodingCalled, "B should be armed");

        h.LatestDecoderFor(b).RaiseFaulted();
        h.LatestDecoderFor(a).RaiseDrained();

        Assert.Same(a, endReachedTrack);
        Assert.Null(h.Coordinator.CurrentTrack);
    }

    [Fact]
    public void Failed_prepare_clears_the_armed_slot_without_starting_decode()
    {
        var h = new Harness();
        var a = T("A");
        var b = T("B");
        h.FailToPrepare(b);
        h.Coordinator.Play(a);
        WaitUntil(() => h.LatestDecoderFor(a).StartDecodingCalled, "A should start");

        h.Coordinator.SetUpcoming(b);

        WaitUntil(() => h.LatestDecoderFor(b).RetireCalled, "B should be cleared after failing to prepare");
        Assert.False(h.LatestDecoderFor(b).StartDecodingCalled);
    }

    [Fact]
    public void Seek_delegates_to_the_current_decoder()
    {
        var h = new Harness();
        var a = T("A");
        h.Coordinator.Play(a);
        WaitUntil(() => h.LatestDecoderFor(a).StartDecodingCalled, "A should start");

        h.Coordinator.Seek(0.5f);

        Assert.Equal(0.5f, h.LatestDecoderFor(a).LastSeekPosition);
    }

    // CurrentTrackBytesProduced is deliberately driven off the shared ring's
    // actual read/write counters, not FakeTrackDecoder.BytesProduced (a
    // plain settable property that doesn't touch any ring at all) - see
    // GaplessCoordinator's _currentTrackReadSplit remarks for why a
    // decode-side counter can't represent real playback position (it can be
    // completely frozen after a handover if decode-ahead already finished).
    // These tests drive the real SharedRing directly to simulate "the
    // decoder wrote N bytes" / "the sink consumed N bytes".
    [Fact]
    public void CurrentTrackBytesProduced_reports_bytes_actually_consumed_from_the_ring_for_a_freshly_played_track()
    {
        var h = new Harness();
        var a = T("A");
        h.Coordinator.Play(a);
        WaitUntil(() => h.LatestDecoderFor(a).StartDecodingCalled, "A should start");

        h.SharedRing.TryWrite(new byte[1000]);
        h.SharedRing.Read(new byte[300]);

        Assert.Equal(300, h.Coordinator.CurrentTrackBytesProduced);
    }

    [Fact]
    public void CurrentTrackBytesProduced_excludes_bytes_from_before_a_natural_handover_promotion()
    {
        var h = new Harness();
        var a = T("A");
        var b = T("B");
        h.Coordinator.Play(a);
        WaitUntil(() => h.LatestDecoderFor(a).StartDecodingCalled, "A should start");
        h.Coordinator.SetUpcoming(b);
        WaitUntil(() => h.LatestDecoderFor(b).StartDecodingCalled, "B should be armed and start decode-ahead");

        // A wrote 1000 bytes total and the sink has consumed 600 of them so
        // far - 400 of A's tail is still sitting in the ring, unread, at
        // the moment of the handover below.
        h.SharedRing.TryWrite(new byte[1000]);
        h.SharedRing.Read(new byte[600]);

        h.LatestDecoderFor(a).RaiseDrained();
        Assert.Same(b, h.Coordinator.CurrentTrack);

        // Nothing of B has reached the sink yet - elapsed time for the
        // newly-current track should read zero.
        Assert.Equal(0, h.Coordinator.CurrentTrackBytesProduced);

        // Simulate PromoteTarget appending B's already-decoded backlog
        // right after the split, then the sink finishing off A's leftover
        // tail (400 bytes) before it ever reaches B's audio.
        h.SharedRing.TryWrite(new byte[300]);
        h.SharedRing.Read(new byte[400]);
        Assert.Equal(0, h.Coordinator.CurrentTrackBytesProduced);

        // Only bytes consumed *past* the split point - B's own audio -
        // count from here.
        h.SharedRing.Read(new byte[300]);
        Assert.Equal(300, h.Coordinator.CurrentTrackBytesProduced);
    }

    [Fact]
    public void Seek_reports_the_seek_target_immediately_then_grows_from_there()
    {
        var h = new Harness();
        var a = T("A");
        h.Coordinator.Play(a);
        WaitUntil(() => h.LatestDecoderFor(a).StartDecodingCalled, "A should start");

        h.Coordinator.Seek(0.5f);

        // A is 3 minutes (see T()); half way in is 90s worth of canonical
        // PCM - reported immediately, before any post-seek audio has
        // actually reached the sink.
        var targetBytes = (long)(90 * GaplessFormat.SampleRate * GaplessFormat.BytesPerFrame);
        Assert.Equal(targetBytes, h.Coordinator.CurrentTrackBytesProduced);
        Assert.Equal(0.5f, h.LatestDecoderFor(a).LastSeekPosition);

        // Once real audio starts flowing again post-seek, it adds on top of
        // the target instead of restarting from zero.
        h.SharedRing.TryWrite(new byte[300]);
        h.SharedRing.Read(new byte[300]);
        Assert.Equal(targetBytes + 300, h.Coordinator.CurrentTrackBytesProduced);
    }

    [Fact]
    public void A_settled_seek_re_anchors_position_onto_where_the_demuxer_actually_landed()
    {
        var h = new Harness();
        var a = T("A");
        h.Coordinator.Play(a);
        WaitUntil(() => h.LatestDecoderFor(a).StartDecodingCalled, "A should start");

        h.Coordinator.Seek(0.5f);
        var targetBytes = (long)(90 * GaplessFormat.SampleRate * GaplessFormat.BytesPerFrame);
        Assert.Equal(targetBytes, h.Coordinator.CurrentTrackBytesProduced);

        // The demuxer put it two seconds short of the request - a lossy
        // seek landing on the nearest frame boundary it could use.
        var landedBytes = targetBytes - (long)(2 * GaplessFormat.SampleRate * GaplessFormat.BytesPerFrame);
        h.LatestDecoderFor(a).RaiseSeekSettled(landedBytes);

        Assert.Equal(landedBytes, h.Coordinator.CurrentTrackBytesProduced);

        // And it grows from the real landing point, not the request.
        h.SharedRing.TryWrite(new byte[300]);
        h.SharedRing.Read(new byte[300]);
        Assert.Equal(landedBytes + 300, h.Coordinator.CurrentTrackBytesProduced);
    }

    [Fact]
    public void A_settled_seek_accounts_for_audio_the_sink_already_drained_since_the_flush()
    {
        var h = new Harness();
        var a = T("A");
        h.Coordinator.Play(a);
        WaitUntil(() => h.LatestDecoderFor(a).StartDecodingCalled, "A should start");

        h.Coordinator.Seek(0.5f);

        // The settle arrives after the sink has already consumed 500 bytes
        // of post-seek audio, so the landing point is 500 bytes back from
        // where the ring is now - not where it is now.
        h.SharedRing.TryWrite(new byte[500]);
        h.SharedRing.Read(new byte[500]);

        var landedBytes = (long)(88 * GaplessFormat.SampleRate * GaplessFormat.BytesPerFrame);
        h.LatestDecoderFor(a).RaiseSeekSettled(landedBytes);

        Assert.Equal(landedBytes + 500, h.Coordinator.CurrentTrackBytesProduced);
    }

    [Fact]
    public void A_settled_seek_from_a_decoder_that_is_no_longer_current_is_ignored()
    {
        var h = new Harness();
        var a = T("A");
        var b = T("B");
        h.Coordinator.Play(a);
        WaitUntil(() => h.LatestDecoderFor(a).StartDecodingCalled, "A should start");

        var staleDecoder = h.LatestDecoderFor(a);
        h.Coordinator.Seek(0.5f);

        // A hard flush onto B lands before A's seek settles - B's own
        // baseline (a fresh ring, zero elapsed) must survive it.
        h.Coordinator.Play(b);
        WaitUntil(() => h.LatestDecoderFor(b).StartDecodingCalled, "B should start");
        Assert.Equal(0, h.Coordinator.CurrentTrackBytesProduced);

        staleDecoder.RaiseSeekSettled((long)(88 * GaplessFormat.SampleRate * GaplessFormat.BytesPerFrame));

        Assert.Equal(0, h.Coordinator.CurrentTrackBytesProduced);
    }
}
