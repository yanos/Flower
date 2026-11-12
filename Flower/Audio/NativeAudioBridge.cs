using System;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

using Miniaudio;

namespace Flower.Audio
{
    // P/Invoke wrapper over native/miniaudio's flower_audio_bridge - read its
    // header for the design; this file is only the marshalling.
    //
    // The bridge is libflowerbridge, a library of its own on every platform
    // and never part of miniaudio: it includes no miniaudio, and desktop's
    // miniaudio is the Miniaudio-CS NuGet's, which Flower does not build.
    // miniaudio is handed a dataCallback pointer into this library instead.
    // The phones build it beside their miniaudio (native/miniaudio/
    // {android,ios}/build.sh); desktop's six RIDs are checked in beside
    // native/miniaudio/desktop/build.sh and copied next to the app by
    // FlowerBridge.props.
    //
    // Desktop runs it too, though not for the reason it was built. It exists
    // because Mono suspends the render thread for a GC on the phones, which
    // desktop's CoreCLR does not - but it is also where the output stage runs,
    // after the queue rather than before it (finding M), and it is the only
    // render path there is: Flower has no managed render callback to fall
    // back to (AUDIO-QUALITY-PLAN.md, Phase 6).
    internal sealed unsafe class NativeAudioBridge : IAudioBridge
    {
        // libflowerbridge.so, libflowerbridge.dylib, flowerbridge.dll, or
        // flowerbridge.framework on iOS, which MiniaudioSink's resolver and
        // Flower.iOS's DirectPInvoke find.
        internal const string LibraryName = "flowerbridge";

        // flower_audio_bridge_snapshot, field for field.
        [StructLayout(LayoutKind.Sequential)]
        private struct NativeSnapshot
        {
            public ulong CallbackCount;
            public ulong RequestedBytes;
            public ulong RealBytes;
            public ulong SilenceBytes;
            public ulong ShortReadCount;
            public ulong UnderrunCount;
            public ulong LastPcmFingerprint;
            public ulong LastReadBytes;
            public ulong TimedCallbackCount;
            public ulong TotalFrames;
            public ulong InterestingGapCount;
            public ulong RenderOverrunCount;
            public ulong MaxGapNanoseconds;
            public ulong ExpectedNanosecondsAtMaxGap;
            public ulong MaxLateNanoseconds;
            public ulong MaxRenderNanoseconds;
            public uint MaxIdenticalCallbackRun;
            public uint MinFrames;
            public uint MaxFrames;
            public uint PrecedingFramesAtMaxGap;
            public uint CurrentFramesAtMaxGap;
        }

        [DllImport(LibraryName, EntryPoint = "flower_audio_bridge_create")]
        private static extern IntPtr Create(uint capacityFrames, uint channels, uint deviceBytesPerSample, uint sampleRate);

        [DllImport(LibraryName, EntryPoint = "flower_audio_bridge_destroy")]
        private static extern void Destroy(IntPtr bridge);

        [DllImport(LibraryName, EntryPoint = "flower_audio_bridge_data_callback")]
        private static extern IntPtr DataCallback();

        [DllImport(LibraryName, EntryPoint = "flower_audio_bridge_attach")]
        private static extern int Attach(IntPtr bridge, ma_device* device);

        [DllImport(LibraryName, EntryPoint = "flower_audio_bridge_detach")]
        private static extern void Detach(IntPtr bridge);

        [DllImport(LibraryName, EntryPoint = "flower_audio_bridge_capacity")]
        private static extern uint NativeCapacity(IntPtr bridge);

        [DllImport(LibraryName, EntryPoint = "flower_audio_bridge_available")]
        private static extern uint NativeAvailable(IntPtr bridge);

        [DllImport(LibraryName, EntryPoint = "flower_audio_bridge_write")]
        private static extern uint NativeWrite(IntPtr bridge, byte* data, uint byteCount);

        [DllImport(LibraryName, EntryPoint = "flower_audio_bridge_request_flush")]
        private static extern ulong NativeRequestFlush(IntPtr bridge);

        [DllImport(LibraryName, EntryPoint = "flower_audio_bridge_flush_acked")]
        private static extern ulong NativeFlushAcked(IntPtr bridge);

        [DllImport(LibraryName, EntryPoint = "flower_audio_bridge_flush_now")]
        private static extern void NativeFlushNow(IntPtr bridge);

        [DllImport(LibraryName, EntryPoint = "flower_audio_bridge_set_primed")]
        private static extern void NativeSetPrimed(IntPtr bridge, int primed);

        [DllImport(LibraryName, EntryPoint = "flower_audio_bridge_begin_fade_in")]
        private static extern void NativeBeginFadeIn(IntPtr bridge, uint fadeFrames);

        [DllImport(LibraryName, EntryPoint = "flower_audio_bridge_begin_fade_out")]
        private static extern void NativeBeginFadeOut(IntPtr bridge, uint fadeFrames);

        [DllImport(LibraryName, EntryPoint = "flower_audio_bridge_fade_out_completed")]
        private static extern int NativeFadeOutCompleted(IntPtr bridge);

        [DllImport(LibraryName, EntryPoint = "flower_audio_bridge_set_target_gain")]
        private static extern void NativeSetTargetGain(IntPtr bridge, float gain);

        [DllImport(LibraryName, EntryPoint = "flower_audio_bridge_set_timing")]
        private static extern void NativeSetTiming(IntPtr bridge, uint gainRampFrames, uint declickFadeFrames);

        [DllImport(LibraryName, EntryPoint = "flower_audio_bridge_set_equalizer")]
        private static extern int NativeSetEqualizer(IntPtr bridge, float* coefficients, uint bandCount, float preamp);

        [DllImport(LibraryName, EntryPoint = "flower_audio_bridge_take_snapshot")]
        private static extern void NativeTakeSnapshot(IntPtr bridge, out NativeSnapshot snapshot);

        [DllImport(LibraryName, EntryPoint = "flower_audio_bridge_reset_timing")]
        private static extern void NativeResetTiming(IntPtr bridge);

        private static bool? _isAvailable;

        // MiniaudioSink's static constructor is where iOS on Mono is told
        // where the framework is, for this assembly's imports as well as the
        // binding's. Run before this class's first P/Invoke, so that probing
        // the bridge does not depend on a sink having been made first.
        static NativeAudioBridge() => RuntimeHelpers.RunClassConstructor(typeof(MiniaudioSink).TypeHandle);

        // Probed by calling the cheapest entry point there is and catching the
        // load failure. Every platform Flower ships carries the library, so
        // false is a packaging mistake, which MiniaudioSink refuses to play
        // through rather than render silence: there is no other render path.
        public static bool IsAvailable
        {
            get
            {
                if (_isAvailable is { } known)
                    return known;

                try
                {
                    // The newest entry point too, called on no bridge, which
                    // it ignores. A binary older than the callback timing has
                    // a create() without the sample rate and a smaller
                    // snapshot; without this it would pass the probe and be
                    // handed arguments it reads as something else.
                    NativeResetTiming(IntPtr.Zero);
                    _isAvailable = DataCallback() != IntPtr.Zero;
                }
                catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
                {
                    _isAvailable = false;
                }

                return _isAvailable.Value;
            }
        }

        private readonly IntPtr _handle;
        private bool _disposed;

        private NativeAudioBridge(IntPtr handle) => _handle = handle;

        // Null when the library is missing or the native side could not
        // allocate.
        //
        // The bridge carries the pipeline's float, so its capacity is in the
        // pipeline's frames; the device format is what the native output
        // stage requantises to on the way out, and it and the rate are fixed
        // for the bridge's life because a bridge is made per device open.
        public static NativeAudioBridge? TryCreate(int capacityFrames, DeviceSampleFormat deviceFormat, uint sampleRate)
        {
            if (!IsAvailable || capacityFrames <= 0 || sampleRate == 0)
                return null;

            var handle = Create((uint)capacityFrames, GaplessFormat.Channels, (uint)deviceFormat.BytesPerSample(), sampleRate);
            return handle == IntPtr.Zero ? null : new NativeAudioBridge(handle);
        }

        // The ma_device_config.dataCallback to install in place of a managed
        // one. Typed back to the binding's own function-pointer shape here so
        // that the cast lives in exactly one place.
        public static delegate* unmanaged[Cdecl]<ma_device*, void*, void*, uint, void> RenderCallback =>
            (delegate* unmanaged[Cdecl]<ma_device*, void*, void*, uint, void>)DataCallback();

        // False when the native registry is full, and the callback would
        // then find no bridge for this device and render nothing.
        public bool AttachTo(ma_device* device) => Attach(_handle, device) != 0;

        public void DetachFromDevice() => Detach(_handle);

        public int Capacity => (int)NativeCapacity(_handle);

        public int Available => (int)NativeAvailable(_handle);

        public int Write(ReadOnlySpan<byte> data)
        {
            if (data.IsEmpty)
                return 0;

            fixed (byte* source = data)
            {
                return (int)NativeWrite(_handle, source, (uint)data.Length);
            }
        }

        public long RequestFlush() => (long)NativeRequestFlush(_handle);

        public long FlushAcked => (long)NativeFlushAcked(_handle);

        public void FlushNow() => NativeFlushNow(_handle);

        public void SetPrimed(bool primed) => NativeSetPrimed(_handle, primed ? 1 : 0);

        public void BeginFadeIn(int fadeFrames) => NativeBeginFadeIn(_handle, (uint)Math.Max(0, fadeFrames));

        public void BeginFadeOut(int fadeFrames) => NativeBeginFadeOut(_handle, (uint)Math.Max(0, fadeFrames));

        public bool FadeOutCompleted => NativeFadeOutCompleted(_handle) != 0;

        // The output stage's controls, run by the native callback - see
        // flower_audio_bridge.h. Not on IAudioBridge: they are the sink's to
        // set, and the feeder never touches them.
        public void SetTargetGain(float gain) => NativeSetTargetGain(_handle, gain);

        public void SetTiming(int gainRampFrames, int declickFadeFrames) =>
            NativeSetTiming(_handle, (uint)Math.Max(0, gainRampFrames), (uint)Math.Max(0, declickFadeFrames));

        // Single-producer on the native side; MiniaudioSink serialises its
        // callers.
        public void SetEqualizer(Equalizer? equalizer)
        {
            if (equalizer == null)
            {
                NativeSetEqualizer(_handle, null, 0, 1f);
                return;
            }

            Span<float> coefficients = stackalloc float[Equalizer.BandCount * Equalizer.CoefficientsPerBand];
            equalizer.CopyCoefficientsTo(coefficients);
            fixed (float* source = coefficients)
            {
                NativeSetEqualizer(_handle, source, (uint)equalizer.ActiveBandCount, equalizer.PreampLinear);
            }
        }

        public AudioBridgeSnapshot TakeSnapshot()
        {
            NativeTakeSnapshot(_handle, out var snapshot);
            return new AudioBridgeSnapshot(
                (long)snapshot.CallbackCount,
                (long)snapshot.RequestedBytes,
                (long)snapshot.RealBytes,
                (long)snapshot.SilenceBytes,
                (long)snapshot.ShortReadCount,
                (long)snapshot.UnderrunCount,
                (long)snapshot.LastPcmFingerprint,
                (long)snapshot.LastReadBytes,
                (int)snapshot.MaxIdenticalCallbackRun,
                new AudioCallbackTimingSnapshot(
                    (long)snapshot.InterestingGapCount,
                    (long)snapshot.RenderOverrunCount,
                    (long)snapshot.TimedCallbackCount,
                    (long)snapshot.TotalFrames,
                    (long)snapshot.MaxGapNanoseconds,
                    (long)snapshot.ExpectedNanosecondsAtMaxGap,
                    (long)snapshot.MaxLateNanoseconds,
                    (long)snapshot.MaxRenderNanoseconds,
                    (int)snapshot.MinFrames,
                    (int)snapshot.MaxFrames,
                    (int)snapshot.PrecedingFramesAtMaxGap,
                    (int)snapshot.CurrentFramesAtMaxGap));
        }

        // The gap across a pause is the device not running, not the audio
        // thread being late, so the sink forgets the last callback around
        // every stop and start. Not on IAudioBridge, like the controls: the
        // feeder never touches it.
        public void ResetTiming() => NativeResetTiming(_handle);

        public void Dispose()
        {
            if (_disposed)
                return;

            _disposed = true;
            Destroy(_handle);
        }
    }
}
