using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Flower.Manager;
using Flower.Models;

namespace Flower.Tests;

// Exercises GaplessCoordinator's handover/idempotency/generation state
// machine against a fake ITrackDecoder, so these tests never touch real
// LibVLC decode.
public class GaplessCoordinatorTests
{
    private sealed class FakeTrackDecoder : ITrackDecoder
    {
        public Track Track { get; }
        public long BytesProduced { get; set; }
        public bool PrepareResult { get; set; } = true;

        public volatile bool StartDecodingCalled;
        public volatile bool RetireCalled;
        public GaplessRingBuffer? PromotedTo { get; private set; }
        public float? LastSeekPosition { get; private set; }

        public event Action? Drained;
        public event Action? Faulted;

        public FakeTrackDecoder(Track track) => Track = track;

        public Task<bool> PrepareAsync(CancellationToken cancellationToken = default) => Task.FromResult(PrepareResult);
        public void StartDecoding() => StartDecodingCalled = true;
        public void Seek(float position) => LastSeekPosition = position;
        public void PromoteTarget(GaplessRingBuffer newTarget) => PromotedTo = newTarget;
        public void Retire() => RetireCalled = true;
        public void Dispose()
        {
        }

        public void RaiseDrained() => Drained?.Invoke();
        public void RaiseFaulted() => Faulted?.Invoke();
    }

    private sealed class Harness
    {
        public GaplessCoordinator Coordinator { get; }
        public GaplessRingBuffer SharedRing { get; } = new(1024);

        private readonly Dictionary<Track, List<FakeTrackDecoder>> _decoders = [];
        private readonly HashSet<Track> _failToPrepare = [];

        public Harness()
        {
            Coordinator = new GaplessCoordinator(SharedRing, (track, ring) =>
            {
                var fake = new FakeTrackDecoder(track) { PrepareResult = !_failToPrepare.Contains(track) };
                if (!_decoders.TryGetValue(track, out var list))
                    _decoders[track] = list = [];
                list.Add(fake);
                return fake;
            });
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

    [Fact]
    public void Faulted_current_track_behaves_the_same_as_Drained()
    {
        var h = new Harness();
        var a = T("A");
        Track? endReachedTrack = null;
        h.Coordinator.EndReached += t => endReachedTrack = t;
        h.Coordinator.Play(a);
        WaitUntil(() => h.LatestDecoderFor(a).StartDecodingCalled, "A should start");

        h.LatestDecoderFor(a).RaiseFaulted();

        Assert.Same(a, endReachedTrack);
        Assert.Null(h.Coordinator.CurrentTrack);
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
}
