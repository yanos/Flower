using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using Flower.Audio;

namespace Flower.Tests.TestSupport;

// A faithful stand-in for MiniaudioSink's render callback, as opposed to
// FakeAudioSink's convenient one.
//
// FakeAudioSink pumps the ring with ReadBlocking, so it simply waits whenever
// the ring is empty and captures a perfectly continuous stream no matter what
// the decode side did. That is what makes it useful for testing the state
// machine - and it is also why starvation, stale post-flush audio and cut-off
// tails were invisible to every test in the suite: the three failure shapes
// the render path was actually reported to have.
//
// This one behaves the way the real callback does. It pulls a fixed-size
// period with the non-blocking Read(), silence-pads a short read exactly as
// DataCallback does, runs the captured bytes through a real OutputStage, and
// records which periods came up short. What it captures is therefore what a
// listener would have heard, silence and all.
public sealed class RenderPumpSink : IAudioSink
{
    private readonly object _gate = new();
    private readonly MemoryStream _captured = new();
    private readonly int _periodFrames;
    private readonly OutputStage _outputStage = new(GaplessFormat.SampleRate);

    private GaplessRingBuffer? _ring;
    private CancellationTokenSource? _pumpCts;
    private Task? _pump;

    // periodFrames defaults to 10ms, small enough that a test's timings stay
    // fine-grained and large enough that the pump isn't spinning.
    public RenderPumpSink(int periodFrames = 480, bool processOutputStage = false)
    {
        _periodFrames = periodFrames;
        ProcessOutputStage = processOutputStage;

        // Off by default: most tests want to assert on the exact bytes the
        // decode side produced, and the output stage's fades and dither would
        // (correctly) change them. Turned on for tests about the output stage
        // itself in the pipeline.
        _outputStage.Timing = new AudioTimingSettings { PrebufferMs = 0, DeclickFadeMs = 0, GainRampMs = 0, TransportFadeMs = 0, FadeOutWaitMs = 0 };
    }

    public bool ProcessOutputStage { get; }

    public event EventHandler? Playing;
    public event EventHandler? Paused;
    public event EventHandler? Stopped;
    public event EventHandler? OutputDeviceLost;

    public bool IsPlaying { get; private set; }
    public int Volume { get; set; } = 100;

    // Every period that could not be filled from the ring - the real
    // definition of a dropout, and the number FakeAudioSink can never report
    // because it waits instead.
    public long ShortPeriods => Interlocked.Read(ref _shortPeriods);
    private long _shortPeriods;

    // Periods rendered as complete silence: the ring had nothing at all.
    public long SilentPeriods => Interlocked.Read(ref _silentPeriods);
    private long _silentPeriods;

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
    public AudioTimingSettings? AppliedTiming { get; private set; }

    public void Start(GaplessRingBuffer ringBuffer) => _ring = ringBuffer;

    public void ApplyEqualizer(Equalizer? equalizer)
    {
        AppliedEqualizer = equalizer;
        _outputStage.Equalizer = equalizer;
    }

    public void ApplyTiming(AudioTimingSettings timing)
    {
        AppliedTiming = timing;
        _outputStage.Timing = timing;
    }

    public IReadOnlyList<AudioOutputDevice> OutputDevices { get; set; } = [];
    public string? OutputDeviceId { get; private set; }
    public IReadOnlyList<AudioOutputDevice> GetOutputDevices() => OutputDevices;

    public void SetOutputDevice(string? deviceId) =>
        OutputDeviceId = deviceId is null || OutputDevices.Any(d => d.Id == deviceId) ? deviceId : null;

    public long BufferedBytes => 0;

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
            _pump = Task.Run(() => Pump(ring, cts.Token));
        }

        Playing?.Invoke(this, EventArgs.Empty);
    }

    private void Pump(GaplessRingBuffer ring, CancellationToken token)
    {
        var period = new byte[_periodFrames * GaplessFormat.BytesPerFrame];
        var delayMs = Math.Max(1, _periodFrames * 1000 / (int)GaplessFormat.SampleRate);

        while (!token.IsCancellationRequested)
        {
            var generation = ring.Generation;

            // Exactly what DataCallback does: one non-blocking read, then
            // silence for whatever it did not get.
            var read = ring.Read(period);
            if (read < period.Length)
            {
                Array.Clear(period, read, period.Length - read);
                Interlocked.Increment(ref _shortPeriods);
                if (read == 0)
                    Interlocked.Increment(ref _silentPeriods);
            }

            if (ProcessOutputStage)
                _outputStage.Process(period, generation);

            lock (_gate)
                _captured.Write(period);

            // Paced to real playback speed, so decode-ahead runs ahead of the
            // reader the way it does in production rather than being drained
            // as fast as the CPU allows.
            Thread.Sleep(delayMs);
        }
    }

    public void Pause()
    {
        if (StopPump())
            Paused?.Invoke(this, EventArgs.Empty);
    }

    public void Stop()
    {
        if (StopPump())
            Stopped?.Invoke(this, EventArgs.Empty);
    }

    // Returns only once the pump has actually stopped - see FakeAudioSink's
    // own StopPump for why that matters (the ring is SPSC, so a test reading
    // it directly has to be the only reader).
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

    public void RaiseOutputDeviceLost() => OutputDeviceLost?.Invoke(this, EventArgs.Empty);

    public void Dispose() => StopPump();
}
