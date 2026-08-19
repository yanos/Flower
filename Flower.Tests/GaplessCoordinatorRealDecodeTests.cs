using System;
using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.IO;
using System.Threading;

using Microsoft.Extensions.Logging.Abstractions;

using Flower.Manager;
using Flower.Models;
using Flower.Tests.TestSupport;

using LibVLCSharp.Shared;

namespace Flower.Tests;

// Real GaplessCoordinator + real TrackDecoders decoding synthetic WAV
// fixtures, captured through a FakeAudioSink - proves the actual splice at
// a natural handover is clean (no gap, no repeated/rewound tail), which
// GaplessCoordinatorTests (fake decoder) can't verify since it never
// produces real PCM. Requires a real VLC install, same as TrackDecoderTests.
[Trait("Category", "RequiresLibVLC")]
[Collection("LibVLC")]
public class GaplessCoordinatorRealDecodeTests : IDisposable
{
    private readonly LibVLC _libVLC;
    private readonly string _tempDir;

    public GaplessCoordinatorRealDecodeTests(LibVlcFixture fixture)
    {
        _libVLC = fixture.LibVLC;
        _tempDir = Directory.CreateTempSubdirectory("flower-coordinator-realdecode-tests-").FullName;
    }

    public void Dispose() => Directory.Delete(_tempDir, recursive: true);

    private static Track MakeTrack(string path, TimeSpan duration) =>
        new() { Title = Path.GetFileName(path), Path = path, Duration = duration };

    // This used to be flaky/skipped, for two compounding reasons - both
    // found and fixed via this test, not worked around here:
    //
    // 1. With one shared LibVLC core, two concurrent MediaPlayers (current
    //    + armed, both SetAudioCallbacks-based) made the current decoder's
    //    OnDrain - and even its higher-level EndReached - silently fail to
    //    fire roughly 80% of the time. Fixed by giving the armed role its
    //    own independent LibVLC core (see GaplessCoordinator's
    //    _secondCore/_cores remarks) so there's no contention to lose
    //    events over in the first place.
    //
    // 2. That fix then reliably exposed a second, genuine bug it had been
    //    masking: a 1s armed track decodes fully in well under a
    //    millisecond once nothing throttles it (no real playback pacing on
    //    the decode-ahead side - see DefaultStagingCapacityBytes' remarks), so it
    //    routinely finished - and got promoted to current - before its own
    //    ArmAsync had even finished awaiting PrepareAsync. StartDecoding()
    //    only ever got called from ArmAsync's completion, which bailed out
    //    the moment it saw _armed no longer pointed at it (now promoted to
    //    _current instead) - so the newly-current decoder's MediaPlayer was
    //    simply never told to start, and playback stalled forever. Fixed in
    //    ArmAsync directly (see its remarks).
    //
    // Bug 2 is also why this doesn't probe live coordinator state
    // mid-flight: B legitimately can (and often does) finish decoding
    // before A does - that's by design, already covered at the fake-decoder
    // level by GaplessCoordinatorTests' _armedAlreadyDrained tests - so
    // CurrentTrack/a single EndReached snapshot are racy, momentary values
    // here, not the actual thing this test cares about. What this test
    // needs to verify - a clean splice with no gap or repeated tail - only
    // depends on the captured PCM stream, which reflects everything that
    // was ever written regardless of how fast the coordinator's own state
    // raced past it.
    [Fact]
    public void Natural_handover_splices_from_track_A_to_track_B_with_no_gap_or_repeated_tail()
    {
        var durationA = TimeSpan.FromSeconds(1);
        var durationB = TimeSpan.FromSeconds(1);
        const byte markerA = 11;
        const byte markerB = 22;
        var trackA = MakeTrack(SyntheticWav.CreateFile(_tempDir, "a.wav", durationA, SyntheticWav.Marker(markerA)), durationA);
        var trackB = MakeTrack(SyntheticWav.CreateFile(_tempDir, "b.wav", durationB, SyntheticWav.Marker(markerB)), durationB);

        var sharedRing = new GaplessRingBuffer(4 * (int)GaplessFormat.SampleRate * GaplessFormat.BytesPerFrame);
        var coordinator = new GaplessCoordinator(_libVLC, sharedRing, NullLogger<GaplessCoordinator>.Instance, NullLogger<TrackDecoder>.Instance);
        var sink = new FakeAudioSink();
        sink.Start(sharedRing);
        sink.Resume();

        // A queue, not a single field: B can drain and cascade through its
        // own EndReached microseconds after A's, so a plain "last value
        // seen" field can race past the exact instant this test polls it.
        var endReachedTracks = new ConcurrentQueue<Track>();
        coordinator.EndReached += t => endReachedTracks.Enqueue(t);

        coordinator.Play(trackA);
        coordinator.SetUpcoming(trackB);

        // Let capture run a bit past the splice so we have some of B, then
        // stop and inspect everything gathered so far. FakeAudioSink paces
        // itself to real time regardless of how fast decode-ahead finished,
        // so this reliably takes about durationA + 0.2s of wall-clock time.
        var targetBytes = (long)((durationA.TotalSeconds + 0.2) * GaplessFormat.SampleRate * GaplessFormat.BytesPerFrame);
        Assert.True(SpinWait.SpinUntil(() => sink.CapturedCount >= targetBytes, TimeSpan.FromSeconds(10)));
        sink.Pause();

        Assert.Contains(trackA, endReachedTracks);

        var captured = sink.Captured;
        Assert.Equal(0, captured.Length % GaplessFormat.BytesPerFrame);

        var frameCount = captured.Length / GaplessFormat.BytesPerFrame;
        int? firstBFrame = null;
        for (var i = 0; i < frameCount; i++)
        {
            var frame = captured.AsSpan(i * GaplessFormat.BytesPerFrame, 2);
            var sample = BinaryPrimitives.ReadInt16LittleEndian(frame);

            Assert.True(sample is markerA or markerB, $"frame {i} had unexpected sample {sample}, splice likely corrupted");

            if (sample == markerB)
            {
                firstBFrame ??= i;
            }
            else if (firstBFrame != null)
            {
                Assert.Fail($"frame {i} reverted back to A's marker after B had already started at frame {firstBFrame} - a repeated/rewound tail at the splice");
            }
        }

        Assert.NotNull(firstBFrame);
        Assert.True(firstBFrame > 0, "expected to capture some of A before B appears");

        coordinator.Dispose();
        sink.Dispose();
    }
    // The deadlock this covers, seen in a real session log: the armed
    // decoder had six minutes of decode-ahead, so it filled its 60s staging
    // ring and parked inside the ring's blocking write - while holding the
    // lock that the handover's own PromoteTarget needed. Promotion never
    // completed, so the shared ring was never fed again: the next track
    // showed as playing, the sink underran forever, and the music simply
    // stopped. Any current track longer than the staging ring hits it.
    //
    // Reproduced here by shrinking the staging ring to a fraction of a
    // second rather than by using a minute-long fixture - the ratio of
    // staging capacity to current-track length is the only thing that
    // matters. RetargetableRingWriterTests covers the same collision
    // deterministically at the unit level; this proves the whole real
    // pipeline (two LibVLC cores, real PCM, a real-time-paced sink) still
    // gets from A to B when it happens.
    [Fact]
    public void Handover_still_completes_when_decode_ahead_filled_the_staging_ring()
    {
        var durationA = TimeSpan.FromSeconds(2);
        var durationB = TimeSpan.FromSeconds(2);
        const byte markerA = 33;
        const byte markerB = 44;
        var trackA = MakeTrack(SyntheticWav.CreateFile(_tempDir, "full-a.wav", durationA, SyntheticWav.Marker(markerA)), durationA);
        var trackB = MakeTrack(SyntheticWav.CreateFile(_tempDir, "full-b.wav", durationB, SyntheticWav.Marker(markerB)), durationB);

        // A tenth of a second of staging against a two-second current
        // track: B's decode-ahead fills it and parks long before A ends.
        var stagingCapacity = (int)GaplessFormat.SampleRate / 10 * GaplessFormat.BytesPerFrame;
        var sharedRing = new GaplessRingBuffer(4 * (int)GaplessFormat.SampleRate * GaplessFormat.BytesPerFrame);
        var coordinator = new GaplessCoordinator(
            _libVLC,
            sharedRing,
            NullLogger<GaplessCoordinator>.Instance,
            NullLogger<TrackDecoder>.Instance,
            stagingCapacity);
        var sink = new FakeAudioSink();
        sink.Start(sharedRing);
        sink.Resume();

        coordinator.Play(trackA);
        coordinator.SetUpcoming(trackB);

        // Deliberately well past the 0.1s of B that was staged at the
        // moment of promotion: before the fix the parked write never
        // resumed, so playback stopped at the end of that backlog. Getting
        // a full second of B out the other side is the actual proof that
        // the promoted decoder kept producing.
        var targetBytes = (long)((durationA.TotalSeconds + 1.0) * GaplessFormat.SampleRate * GaplessFormat.BytesPerFrame);
        Assert.True(
            SpinWait.SpinUntil(() => sink.CapturedCount >= targetBytes, TimeSpan.FromSeconds(15)),
            "playback stalled after the handover - the promoted decoder never resumed feeding the shared ring");

        sink.Pause();

        var captured = sink.Captured;
        var frameCount = captured.Length / GaplessFormat.BytesPerFrame;
        int? firstBFrame = null;
        var bFrames = 0;
        for (var i = 0; i < frameCount; i++)
        {
            var sample = BinaryPrimitives.ReadInt16LittleEndian(captured.AsSpan(i * GaplessFormat.BytesPerFrame, 2));
            Assert.True(sample is markerA or markerB, $"frame {i} had unexpected sample {sample}, splice likely corrupted");

            if (sample == markerB)
            {
                firstBFrame ??= i;
                bFrames++;
            }
            else if (firstBFrame != null)
            {
                Assert.Fail($"frame {i} reverted back to A's marker after B had already started at frame {firstBFrame} - a repeated/rewound tail at the splice");
            }
        }

        Assert.NotNull(firstBFrame);
        Assert.True(
            bFrames > stagingCapacity / GaplessFormat.BytesPerFrame * 2,
            $"only {bFrames} frames of B came through - about the size of the staged backlog, so the promoted decoder stopped producing after the handover");

        coordinator.Dispose();
        sink.Dispose();
    }
}
