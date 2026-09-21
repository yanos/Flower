using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using Flower.Audio;
using Flower.Models;

using Xunit;

namespace Flower.Tests;

// A gapless handover moves the promoted track's staged audio - up to a minute
// of it - into the shared ring at playback speed. From a phone log: a tap on
// another song during that minute froze the app for 32 seconds, because
// Play() retired the promoted decoder under the coordinator's lock, and
// retiring waited for the writer's lock the drain was holding. And every
// handover logged "did not stop within 5s; leaking its decoder", because the
// drain ran on the finished decoder's own thread, which its Retire() joins.
public class GaplessHandoverResponsivenessTests
{
    private const int SharedBytes = 64;
    private const int StagingBytes = 4096;

    private static Track T(string title) =>
        new() { Title = title, Path = $"/music/{title}.flac", Duration = TimeSpan.FromSeconds(1) };

    // Shaped like FfmpegTrackDecoder where it matters here: its own thread,
    // Drained raised from that thread, a retire that wakes the writer and
    // joins the thread off the caller's.
    private sealed class FillDecoder(Track track, GaplessRingBuffer ring, byte fill, int totalBytes, ManualResetEventSlim? holdBeforeDrain = null)
        : ITrackDecoder
    {
        private readonly RetargetableRingWriter _writer = new(ring);
        private Thread? _thread;
        private int _retired;
        private long _written;

        public Track Track { get; } = track;
        public long BytesProduced => Interlocked.Read(ref _written);
        public GaplessRingBuffer Target => _writer.Target;
        public TaskCompletionSource<bool> Joined { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public event Action? Drained;
        public event Action? Faulted { add { } remove { } }
        public event Action<long>? SeekSettled { add { } remove { } }

        private bool IsRetired => Volatile.Read(ref _retired) == 1;

        public Task<DecodePrepareResult> PrepareAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(DecodePrepareResult.Ready);

        public void StartDecoding()
        {
            _thread = new Thread(Decode) { IsBackground = true };
            _thread.Start();
        }

        private void Decode()
        {
            var chunk = Enumerable.Repeat(fill, 16).ToArray();
            while (!IsRetired && BytesProduced < totalBytes)
            {
                _writer.Write(chunk, () => IsRetired);
                Interlocked.Add(ref _written, chunk.Length);
            }

            holdBeforeDrain?.Wait(TimeSpan.FromSeconds(10));
            if (!IsRetired)
                Drained?.Invoke();
        }

        public void Seek(float position)
        {
        }

        public PromotionSplice PrimeTarget(GaplessRingBuffer newTarget) => _writer.PrimeTarget(newTarget);

        public PromotionSplice PromoteTarget(GaplessRingBuffer newTarget) => _writer.PromoteTarget(newTarget);

        public void Retire()
        {
            if (Interlocked.Exchange(ref _retired, 1) == 1)
                return;

            _writer.Wake();
            var thread = _thread;
            _ = Task.Run(() => Joined.TrySetResult(thread is null || thread.Join(TimeSpan.FromSeconds(5))));
        }

        public void Dispose() => Retire();
    }

    private static void WaitFor(Func<bool> condition, string because)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(5);
        while (!condition())
        {
            Assert.True(DateTime.UtcNow < deadline, because);
            Thread.Sleep(1);
        }
    }

    [Fact]
    public void A_song_picked_during_a_handover_starts_at_once_and_the_finished_decoder_is_released()
    {
        // Nothing reads the shared ring: playback is what paces the drain,
        // and a ring nobody reads is a drain that never ends on its own.
        var shared = new GaplessRingBuffer(SharedBytes);
        using var releaseFirst = new ManualResetEventSlim(false);
        FillDecoder? first = null, promoted = null, picked = null;

        var coordinator = new GaplessCoordinator(shared, (track, ring) => track.Title switch
        {
            "first" => first = new FillDecoder(track, ring, 0xA1, 16, releaseFirst),
            "promoted" => promoted = new FillDecoder(track, ring, 0xB2, StagingBytes * 2),
            _ => picked = new FillDecoder(track, ring, 0xC3, int.MaxValue),
        }, stagingCapacityBytes: StagingBytes);

        coordinator.Play(T("first"));
        coordinator.SetUpcoming(T("promoted"));
        WaitFor(() => promoted?.Target is { AvailableBytes: StagingBytes }, "the armed decoder never filled its staging ring");

        releaseFirst.Set();
        WaitFor(() => shared.AvailableBytes == SharedBytes && first!.Joined.Task.IsCompleted,
            "the handover never started, or the finished decoder was never retired");

        Assert.True(first!.Joined.Task.Result, "the finished decoder's thread was still busy with the handover when its retire joined it");

        var play = Task.Run(() => coordinator.Play(T("picked")), TestContext.Current.CancellationToken);
        // Ten seconds for something that takes microseconds, because the number
        // is not measuring the drain - a Play that waited for it would not
        // finish at all here, since nothing drains the 64-byte shared ring while
        // this assertion is the thing waiting. So the only job of the budget is
        // to clear scheduling noise, and the 2 seconds it used to allow was the
        // tightest in this file (WaitFor above gives 5) and the one that failed
        // a full parallel run on a busy machine. A larger number does not make
        // the assertion weaker; it makes it about what it says it is about.
        Assert.True(play.Wait(TimeSpan.FromSeconds(10)), "Play waited for the handover's drain");

        // What the picked song hears is the picked song, not the rest of the
        // promoted one's backlog arriving behind it.
        WaitFor(() => shared.AvailableBytes == SharedBytes, "the picked song never reached the shared ring");
        Thread.Sleep(50);
        var buffer = new byte[SharedBytes];
        var read = shared.Read(buffer);
        Assert.All(buffer[..read], b => Assert.Equal(0xC3, b));

        coordinator.Stop();
        Assert.NotNull(picked);
    }
}
