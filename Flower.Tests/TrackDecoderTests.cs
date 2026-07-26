using System;
using System.Buffers.Binary;
using System.IO;
using System.Threading;

using Flower.Manager;
using Flower.Models;
using Flower.Tests.TestSupport;

using LibVLCSharp.Shared;

namespace Flower.Tests;

// Real LibVLC decode against synthetic WAV fixtures - the layer above
// (GaplessCoordinatorTests) fakes ITrackDecoder entirely, so this is the
// only place TrackDecoder's actual LibVLC-driven behavior gets exercised.
// Requires a real VLC install, same as the app itself on macOS/Linux.
[Trait("Category", "RequiresLibVLC")]
[Collection("LibVLC")]
public class TrackDecoderTests : IDisposable
{
    private readonly LibVLC _libVLC;
    private readonly string _tempDir;

    public TrackDecoderTests(LibVlcFixture fixture)
    {
        _libVLC = fixture.LibVLC;
        _tempDir = Directory.CreateTempSubdirectory("flower-trackdecoder-tests-").FullName;
    }

    public void Dispose() => Directory.Delete(_tempDir, recursive: true);

    private static Track MakeTrack(string path, TimeSpan duration) =>
        new() { Title = "Test", Path = path, Duration = duration };

    private static int BytesFor(TimeSpan duration) =>
        (int)(duration.TotalSeconds * GaplessFormat.SampleRate) * GaplessFormat.BytesPerFrame;

    private static void WaitUntil(Func<bool> condition, string because) =>
        Assert.True(SpinWait.SpinUntil(condition, TimeSpan.FromSeconds(10)), because);

    [Fact]
    public void Decoding_a_short_track_advances_BytesProduced_and_fires_Drained()
    {
        var duration = TimeSpan.FromSeconds(1);
        var path = SyntheticWav.CreateFile(_tempDir, "a.wav", duration, SyntheticWav.Marker(0x11));
        var ring = new GaplessRingBuffer(BytesFor(duration) + 4096);
        var decoder = new TrackDecoder(_libVLC, MakeTrack(path, duration), ring);

        var drained = false;
        decoder.Drained += () => drained = true;

        decoder.StartDecoding();

        // Regression coverage for this session's "stuck at the end" bug:
        // Drained must eventually fire (whether via the real OnDrain
        // callback or the EndReached fallback), not hang forever.
        WaitUntil(() => drained, "Drained should fire once a short track finishes decoding");
        Assert.InRange((double)decoder.BytesProduced, BytesFor(duration) * 0.9, BytesFor(duration) * 1.1);

        decoder.Retire();
    }

    [Fact]
    public void Faulted_fires_for_a_missing_file()
    {
        var missingPath = Path.Combine(_tempDir, "does-not-exist.wav");
        var ring = new GaplessRingBuffer(65536);
        var decoder = new TrackDecoder(_libVLC, MakeTrack(missingPath, TimeSpan.FromSeconds(1)), ring);

        var faulted = false;
        decoder.Faulted += () => faulted = true;

        decoder.StartDecoding();

        WaitUntil(() => faulted, "Faulted should fire for a missing file");

        decoder.Retire();
    }

    [Fact]
    public void Seek_lands_decoded_content_at_the_expected_half_of_the_track()
    {
        var duration = TimeSpan.FromSeconds(2);
        var splitFrame = (int)(duration.TotalSeconds * 0.5 * GaplessFormat.SampleRate);
        const short firstHalfMarker = 11;
        const short secondHalfMarker = 22;
        var path = SyntheticWav.CreateFile(_tempDir, "split.wav", duration,
            frame => frame < splitFrame ? firstHalfMarker : secondHalfMarker);
        var ring = new GaplessRingBuffer(BytesFor(duration) + 4096);
        var decoder = new TrackDecoder(_libVLC, MakeTrack(path, duration), ring);

        decoder.StartDecoding();
        WaitUntil(() => decoder.BytesProduced > 0, "decode should start producing");

        decoder.Seek(0.5f);

        short? lastSample = null;
        var settled = SpinWait.SpinUntil(() =>
        {
            var chunk = new byte[GaplessFormat.BytesPerFrame];
            if (ring.Read(chunk) <= 0)
                return false;
            lastSample = BinaryPrimitives.ReadInt16LittleEndian(chunk);
            return lastSample == secondHalfMarker;
        }, TimeSpan.FromSeconds(10));

        Assert.True(settled, $"expected post-seek PCM to land in the second half (marker {secondHalfMarker}), last saw {lastSample}");

        decoder.Retire();
    }

    [Fact]
    public void PromoteTarget_drains_the_buffered_backlog_then_keeps_writing_to_the_new_target()
    {
        // A short synthetic clip with a ring big enough to hold it whole
        // decodes essentially as fast as the CPU allows (nothing throttles
        // it to real playback time here - that only happens via a small
        // ring's backpressure), so this test can't assume decode is still
        // gradually in flight by the time it promotes; instead it checks
        // the outcome once decode has actually finished, regardless of how
        // much of it happened before vs after PromoteTarget was called.
        var duration = TimeSpan.FromSeconds(1);
        var path = SyntheticWav.CreateFile(_tempDir, "promote.wav", duration, SyntheticWav.Marker(0x33));
        var stagingRing = new GaplessRingBuffer(BytesFor(duration) + 4096);
        var decoder = new TrackDecoder(_libVLC, MakeTrack(path, duration), stagingRing);

        var drained = false;
        decoder.Drained += () => drained = true;

        decoder.StartDecoding();
        WaitUntil(() => decoder.BytesProduced > 0, "decode should start producing");

        var newTarget = new GaplessRingBuffer(BytesFor(duration) + 4096);
        decoder.PromoteTarget(newTarget);

        WaitUntil(() => newTarget.AvailableBytes > 0, "the already-buffered backlog should have moved to the new target");
        WaitUntil(() => drained, "decode should finish");
        WaitUntil(() => newTarget.AvailableBytes >= BytesFor(duration) * 0.9, "the whole clip should end up in the new target");

        // Nothing should have landed back in the abandoned staging ring
        // after the retarget - PromoteTarget and OnPlay share the same
        // lock, so there's no window for that to happen.
        Assert.Equal(0, stagingRing.AvailableBytes);

        decoder.Retire();
    }
}
