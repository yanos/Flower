using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

using Flower.Audio;

using Miniaudio;

namespace Flower.Tests.Decoding.Scenarios;

// Is the output stage Flower runs in C the one every test checks in C#?
//
// On every platform the render callback is C (native/miniaudio/
// flower_audio_bridge.c, built as libflowerbridge): the EQ, the volume ramp, the declick, the dither
// and the requantisation all run there, as a port of Flower/Audio/OutputStage.cs
// that promises to match it sample for sample. Everything that tests that
// arithmetic - OutputStageTests, EqualizerTests, the PCM oracle - tests the
// C#. These hold the C to it: the same input and the same control changes go
// through both, and every byte the device would be handed is compared.
//
// Bit for bit rather than within a tolerance, because a tolerance would hide
// exactly what goes wrong in a port like this. A compiler fusing a multiply
// and an add that RyuJIT keeps apart (the bridge's FP_CONTRACT OFF is there for
// that), or rounding half away from zero where MathF.Round goes to even, moves
// a sample by one LSB now and then. Nobody hears it, and it still means the C
// is no longer what every other test checked.
//
// No audio device: the bridge uses the ma_device pointer only as a key to
// find itself and never dereferences it, so the callback is called directly
// with a dummy one. What runs is the compiled callback in the libflowerbridge
// the app ships, on the machine's own CPU, which is the point: the phone
// runners check the phones' builds, and NativeOutputStageTests the desktop
// ones, on whichever desktop CI or a developer runs it.
public static class NativeOutputStageChecks
{
    private const uint SampleRate = 48000;

    // flower_audio_bridge.c's FLOWER_AUDIO_BRIDGE_CHUNK_FRAMES. A callback
    // larger than this is processed in pieces, and the C# is handed the same
    // pieces: the gain ramp's step and an EQ crossfade's length are both per
    // piece, which is the one documented difference from a single pass.
    private const int NativeChunkFrames = 4096;

    private const int BridgeCapacityFrames = 8192;

    // About two seconds per scenario, as the throwaway harness that first
    // checked the port ran.
    private const long FramesPerScenario = 2 * SampleRate;

    // Callback sizes, cycled. The edges of the native chunk on both sides, a
    // single frame, and the sizes devices really ask for.
    private static readonly int[] CallbackFrames =
        [512, 1, 2, 3, 441, 4096, 4097, 5000, 64, 1024, 2048, 4095, 7, 960, 1500, 256];

    // The index of the first 5000-frame callback, where an EQ change lands
    // on a callback the C splits.
    private const int FirstSplitCallback = 7;

    public static IReadOnlyList<CheckResult> RunAll()
    {
        // Flower ships the bridge on every platform it builds for, so a
        // runner without it was packaged wrong. That has to fail rather than
        // quietly check nothing.
        if (!NativeAudioBridge.IsAvailable)
            return
            [
                DecodeChecks.Run("The native output stage is available to be checked at all", () =>
                    throw new CheckFailedException(
                        $"no {NativeAudioBridge.LibraryName} library loaded, or one older than the output stage"))
            ];

        var results = new List<CheckResult>();
        foreach (var format in new[] { DeviceSampleFormat.S16, DeviceSampleFormat.S24 })
        {
            foreach (var scenario in Scenarios())
            {
                var name = $"Native output stage at {format}: {scenario.Name}";
                results.Add(DecodeChecks.Run(name, () => Play(scenario, format)));
            }
        }

        return results;
    }

    private sealed record Scenario(
        string Name,
        Signal Signal,
        AudioTimingSettings Timing,
        EqualizerSettings? Equalizer,
        IReadOnlyDictionary<int, Action<Twin>> Events);

    private static IEnumerable<Scenario> Scenarios()
    {
        var quiet = new Signal(Amplitude: 0.5f, OnSixteenBitGrid: false);
        var loud = new Signal(Amplitude: 0.95f, OnSixteenBitGrid: false);
        var timing = new AudioTimingSettings();
        var none = new Dictionary<int, Action<Twin>>();

        var bassBoost = Eq(preampDb: -3, 9, 6, 3, 0, 0, 0, 0, 0, 0, 0);
        var vocalCut = Eq(preampDb: 0, 0, 0, 0, -4, -8, -6, 0, 2, 4, 6);
        var everythingUp = Eq(preampDb: 6, 12, 12, 12, 12, 12, 12, 12, 12, 12, 12);

        yield return new("a 16-bit source at unity comes out as the same integers",
            new Signal(Amplitude: 0.5f, OnSixteenBitGrid: true), timing, null, none);

        yield return new("a float source at unity is dithered the same",
            quiet, timing, null, none);

        yield return new("the volume ramps the same, mid-play and across callbacks",
            quiet, timing, null, new Dictionary<int, Action<Twin>>
            {
                [3] = twin => twin.SetGain(0.25f),
                [9] = twin => twin.SetGain(0.7f),
                [12] = twin => twin.SetGain(0f),
                [20] = twin => twin.SetGain(1f),
                [27] = twin => twin.SetGain(OutputStage.GainForVolumePercent(37)),
            });

        yield return new("an EQ on from the first buffer filters the same",
            quiet, timing, bassBoost, none);

        yield return new("an EQ swapped mid-play crossfades the same, on a split callback too",
            quiet, timing, bassBoost, new Dictionary<int, Action<Twin>>
            {
                [4] = twin => twin.SetEqualizer(vocalCut),
                [FirstSplitCallback] = twin => twin.SetEqualizer(bassBoost),
                [FirstSplitCallback + 16] = twin => twin.SetEqualizer(vocalCut),
            });

        yield return new("an EQ swapped to and from bypass crossfades the same",
            quiet, timing, null, new Dictionary<int, Action<Twin>>
            {
                [2] = twin => twin.SetEqualizer(vocalCut),
                [6] = twin => twin.SetEqualizer(null),
                [FirstSplitCallback] = twin => twin.SetEqualizer(bassBoost),
                [14] = twin => twin.SetEqualizer(null),
            });

        yield return new("a boost past full scale clips the same",
            loud, timing, everythingUp, new Dictionary<int, Action<Twin>>
            {
                [10] = twin => twin.SetGain(0.5f),
            });

        yield return new("a flush and a rebuffer are declicked the same",
            quiet, timing, vocalCut, new Dictionary<int, Action<Twin>>
            {
                [5] = twin => twin.Flush(silentFrames: 441),
                [FirstSplitCallback] = twin => twin.Rebuffer(silentFrames: 1024),
                [11] = twin => twin.SetGain(0.4f),
                [12] = twin => twin.Flush(silentFrames: 1),
                [19] = twin => twin.Rebuffer(silentFrames: 64),
            });

        yield return new("with the ramp and the declick off, a gain change steps the same",
            quiet, new AudioTimingSettings { GainRampMs = 0, DeclickFadeMs = 0 }, bassBoost,
            new Dictionary<int, Action<Twin>>
            {
                [3] = twin => twin.SetGain(0.3f),
                [6] = twin => twin.Flush(silentFrames: 512),
                [9] = twin => twin.SetGain(0.9f),
            });
    }

    private static EqualizerSettings Eq(double preampDb, params double[] bandGainsDb) =>
        new() { Enabled = true, PreampDb = preampDb, BandGainsDb = bandGainsDb };

    private static void Play(Scenario scenario, DeviceSampleFormat format)
    {
        using var twin = new Twin(format, scenario.Timing, scenario.Equalizer);

        long frames = 0;
        for (var callback = 0; frames < FramesPerScenario; callback++)
        {
            if (scenario.Events.TryGetValue(callback, out var change))
                change(twin);

            var size = CallbackFrames[callback % CallbackFrames.Length];
            twin.Render(size, scenario.Signal);
            frames += size;
        }
    }

    // A tone on each channel plus a little noise, as a pure function of the
    // frame index, so the two sides can be handed it a callback at a time and
    // never drift. OnSixteenBitGrid snaps every sample to a 16-bit integer,
    // which is what takes the requantiser's no-dither branch.
    private sealed record Signal(float Amplitude, bool OnSixteenBitGrid)
    {
        public void Fill(long firstFrame, Span<float> interleaved)
        {
            for (var i = 0; i < interleaved.Length; i++)
            {
                var frame = firstFrame + i / 2;
                var channel = i % 2;
                var seconds = frame / (double)SampleRate;
                var hz = channel == 0 ? 440.0 : 997.0;

                var value = (float)(Amplitude * 0.9 * Math.Sin(2 * Math.PI * hz * seconds))
                    + Amplitude * 0.1f * Noise(frame * 2 + channel);

                interleaved[i] = OnSixteenBitGrid ? MathF.Round(value * 32768f) / 32768f : value;
            }
        }

        // -1..1, from a hash of the position rather than a running generator.
        private static float Noise(long position)
        {
            var x = (uint)position * 2654435761u;
            x ^= x >> 15;
            x *= 2246822519u;
            x ^= x >> 13;
            return x / (float)uint.MaxValue * 2f - 1f;
        }
    }

    // The two stages, driven in lockstep. The C# one is called the way
    // MiniaudioSink's managed callback calls it; the C one is fed and
    // controlled the way MiniaudioSink and AudioFeeder feed and control it.
    private sealed unsafe class Twin : IDisposable
    {
        private readonly DeviceSampleFormat _format;
        private readonly OutputStage _stage;
        private readonly NativeAudioBridge _bridge;
        private readonly ma_device* _device;

        private int _generation;
        private long _nextFrame;
        private int _callback;

        // Unity gain before the first buffer, deliberately: a bridge starts
        // its ramp at the target because it is made per device open, while
        // OutputStage outlives its devices and starts at unity. That is the
        // one intended difference between the two, and from unity both agree.
        public Twin(DeviceSampleFormat format, AudioTimingSettings timing, EqualizerSettings? equalizer)
        {
            _format = format;
            _stage = new OutputStage(SampleRate, format) { Timing = timing };
            _bridge = NativeAudioBridge.TryCreate(BridgeCapacityFrames, format)
                ?? throw new CheckFailedException($"flower_audio_bridge_create refused {BridgeCapacityFrames} frames at {format}");

            _device = (ma_device*)NativeMemory.AllocZeroed(1);
            _bridge.AttachTo(_device);

            // As MiniaudioSink.PushTiming converts them, from the clamped
            // settings the stage itself runs on.
            _bridge.SetTiming(Frames(_stage.Timing.GainRampMs), Frames(_stage.Timing.DeclickFadeMs));
            SetGain(1f);
            SetEqualizer(equalizer);
            _bridge.SetPrimed(true);
        }

        public void SetGain(float gain)
        {
            _stage.TargetGain = gain;
            _bridge.SetTargetGain(_stage.TargetGain);
        }

        // A fresh Equalizer for each change, as EqualizerViewModel builds one:
        // the incoming filter starts from zeroed delay lines on both sides.
        public void SetEqualizer(EqualizerSettings? settings)
        {
            var equalizer = settings is null ? null : Equalizer.BuildFrom(settings, SampleRate);
            _stage.Equalizer = equalizer;
            _bridge.SetEqualizer(equalizer);
        }

        // A seek or a skip. The ring's generation changes under the managed
        // callback; the native one acknowledges the flush in its next
        // callback, rendering silence because a flush un-primes it, and plays
        // again once the feeder re-primes.
        public void Flush(int silentFrames)
        {
            _generation++;
            var request = _bridge.RequestFlush();
            RenderSilence(silentFrames, "the callback acknowledging a flush");
            if (_bridge.FlushAcked != request)
                throw new CheckFailedException($"a callback ran and flush {request} is still unacknowledged");

            _bridge.SetPrimed(true);
        }

        // A gap no flush marked. The managed callback skips the stage while
        // the latch is shut and fades in what comes after; the native one
        // arms its declick when it sees the latch reopen.
        public void Rebuffer(int silentFrames)
        {
            _bridge.SetPrimed(false);
            RenderSilence(silentFrames, "a callback while rebuffering");
            _bridge.SetPrimed(true);
            _stage.FadeInNextAudio();
        }

        public void Render(int frames, Signal signal)
        {
            var source = new float[frames * 2];
            signal.Fill(_nextFrame, source);
            _nextFrame += frames;

            var bytesPerSample = _format.BytesPerSample();
            var expected = new byte[source.Length * bytesPerSample];
            var managed = (float[])source.Clone();
            for (var done = 0; done < frames; done += NativeChunkFrames)
            {
                var piece = Math.Min(NativeChunkFrames, frames - done);
                _stage.Process(
                    managed.AsSpan(done * 2, piece * 2),
                    expected.AsSpan(done * 2 * bytesPerSample, piece * 2 * bytesPerSample),
                    _generation);
            }

            var bytes = MemoryMarshal.AsBytes(source.AsSpan());
            var written = _bridge.Write(bytes);
            if (written != bytes.Length)
                throw new CheckFailedException($"the bridge took {written} of {bytes.Length} bytes");

            var actual = CallNative(frames);
            Compare(expected, actual, frames);
            _callback++;
        }

        private void RenderSilence(int frames, string what)
        {
            var actual = CallNative(frames);
            if (actual.AsSpan().IndexOfAnyExcept((byte)0) is var at and >= 0)
                throw new CheckFailedException($"{what} was not silent: byte {at} of {actual.Length} is 0x{actual[at]:X2}");
        }

        // Pre-filled with a pattern no stage writes as a whole buffer, so a
        // callback that did nothing at all reads as that rather than as a
        // first-sample mismatch.
        private byte[] CallNative(int frames)
        {
            var output = new byte[frames * _format.BytesPerFrame()];
            output.AsSpan().Fill(0xA5);
            fixed (byte* destination = output)
            {
                NativeAudioBridge.RenderCallback(_device, destination, null, (uint)frames);
            }

            if (output.AsSpan().IndexOfAnyExcept((byte)0xA5) < 0)
                throw new CheckFailedException(
                    $"callback {_callback} wrote nothing: the bridge did not find itself by its device pointer");

            return output;
        }

        private void Compare(byte[] expected, byte[] actual, int frames)
        {
            var bytesPerSample = _format.BytesPerSample();
            var first = expected.AsSpan().CommonPrefixLength(actual);
            if (first == expected.Length)
                return;

            var sample = first / bytesPerSample;
            var differing = 0;
            for (var i = 0; i < expected.Length; i += bytesPerSample)
            {
                if (!expected.AsSpan(i, bytesPerSample).SequenceEqual(actual.AsSpan(i, bytesPerSample)))
                    differing++;
            }

            throw new CheckFailedException(
                $"callback {_callback} ({frames} frames), frame {sample / 2} channel {sample % 2}: "
                + $"OutputStage wrote {Read(expected, sample)}, the bridge {Read(actual, sample)}; "
                + $"{differing} of {expected.Length / bytesPerSample} samples differ");
        }

        private int Read(byte[] buffer, int sample)
        {
            if (_format == DeviceSampleFormat.S16)
                return BitConverter.ToInt16(buffer, sample * 2);

            var at = sample * 3;
            return (buffer[at] | buffer[at + 1] << 8 | (sbyte)buffer[at + 2] << 16);
        }

        private static int Frames(int milliseconds) => (int)(milliseconds * (long)SampleRate / 1000);

        public void Dispose()
        {
            _bridge.Dispose();
            NativeMemory.Free(_device);
        }
    }
}
