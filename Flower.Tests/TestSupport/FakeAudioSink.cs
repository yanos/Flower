using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

using Flower.Manager;

namespace Flower.Tests.TestSupport;

// Stands in for MiniaudioSink/LibVlcRawStreamSink without touching real
// audio hardware: Resume() spins up a background pump reading
// GaplessRingBuffer.Read/ReadBlocking exactly the way a real render
// callback would, appending everything it gets to an in-memory buffer tests
// can inspect. This is the one seam that makes GaplessAudioManager,
// GaplessCoordinator and PlaylistControlViewModel testable end to end
// without a sound card.
public sealed class FakeAudioSink : IAudioSink
{
    private readonly object _gate = new();
    private readonly MemoryStream _captured = new();
    private GaplessRingBuffer? _ring;
    private CancellationTokenSource? _pumpCts;
    private Task? _pump;

    public event EventHandler? Playing;
    public event EventHandler? Paused;
    public event EventHandler? Stopped;

    public bool IsPlaying { get; private set; }
    public int Volume { get; set; } = 100;

    public byte[] Captured
    {
        get
        {
            lock (_gate)
                return _captured.ToArray();
        }
    }

    public long CapturedCount
    {
        get
        {
            lock (_gate)
                return _captured.Length;
        }
    }

    public Equalizer? AppliedEqualizer { get; private set; }

    public void Start(GaplessRingBuffer ringBuffer) => _ring = ringBuffer;

    public void ApplyEqualizer(Equalizer? equalizer) => AppliedEqualizer = equalizer;

    public void Resume()
    {
        CancellationTokenSource cts;
        GaplessRingBuffer ring;

        lock (_gate)
        {
            if (IsPlaying || _ring is not { } r)
                return;

            IsPlaying = true;
            ring = r;
            cts = _pumpCts = new CancellationTokenSource();
        }

        lock (_gate)
            _pump = Task.Run(() => Pump(ring, cts.Token));

        Playing?.Invoke(this, EventArgs.Empty);
    }

    private void Pump(GaplessRingBuffer ring, CancellationToken token)
    {
        // Paced to roughly real playback speed (matching a real render
        // device pulling PCM at GaplessFormat.SampleRate) rather than
        // draining as fast as the CPU allows - a consumer this fast makes
        // even the *current* decoder's writes race ahead unrealistically
        // quickly (production paces the current decoder to real time via
        // backpressure from a real device's actual consumption rate), which
        // was enough to manufacture an artificial two-decoders-finish-
        // simultaneously race between the current and armed decoder that
        // real playback - where the current track takes its full real
        // duration - wouldn't produce nearly as tightly.
        Span<byte> chunk = stackalloc byte[4096];
        try
        {
            while (!token.IsCancellationRequested)
            {
                var read = ring.ReadBlocking(chunk, token);
                if (read <= 0)
                    continue;

                lock (_gate)
                    _captured.Write(chunk[..read]);

                var frames = read / GaplessFormat.BytesPerFrame;
                var delayMs = frames * 1000 / (int)GaplessFormat.SampleRate;
                if (delayMs > 0)
                    Thread.Sleep(delayMs);
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    public void Pause()
    {
        if (!StopPump())
            return;

        Paused?.Invoke(this, EventArgs.Empty);
    }

    public void Stop()
    {
        if (!StopPump())
            return;

        Stopped?.Invoke(this, EventArgs.Empty);
    }

    // Returns only once the pump thread has actually stopped, not merely
    // once it has been asked to. The ring is single-producer/single-consumer
    // by contract, so a test that reads it directly has to be the only
    // reader - and "Pause() returned" is the only signal it has that the pump
    // is no longer competing for the same bytes. Leaving that asynchronous
    // made GaplessAudioManagerTests' Time/Position assertions flaky: the pump
    // would take one 4096-byte chunk out from under the test's own Read, and
    // the position came out a chunk short.
    private bool StopPump()
    {
        CancellationTokenSource? cts;
        Task? pump;
        lock (_gate)
        {
            if (!IsPlaying)
                return false;

            IsPlaying = false;
            cts = _pumpCts;
            pump = _pump;
            _pumpCts = null;
            _pump = null;
        }

        cts?.Cancel();
        pump?.Wait(TimeSpan.FromSeconds(5));
        return true;
    }

    public void Dispose() => StopPump();
}
