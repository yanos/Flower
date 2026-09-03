using System;
using System.Runtime.InteropServices;

using Miniaudio;

namespace Flower.Audio
{
    // P/Invoke wrapper over native/miniaudio's flower_audio_bridge - read its
    // header for the design; this file is only the marshalling.
    //
    // Only present where Flower builds its own miniaudio: Android and iOS
    // (native/miniaudio/{android,ios}/build.sh). On desktop the binary comes
    // from the Miniaudio-CS NuGet, which of course carries none of these
    // symbols, so IsAvailable is false there and MiniaudioSink keeps its
    // managed render callback. That split is the honest one rather than a
    // temporary state: the stalls this exists to survive are Mono suspending
    // the render thread for a GC, and desktop runs CoreCLR - a full day of
    // macOS client logs contains no late render callback at all, against
    // seven on the phone.
    internal sealed unsafe class NativeAudioBridge : IAudioBridge
    {
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
            public uint MaxIdenticalCallbackRun;
        }

        [DllImport("miniaudio", EntryPoint = "flower_audio_bridge_create")]
        private static extern IntPtr Create(uint capacityBytes, uint bytesPerFrame);

        [DllImport("miniaudio", EntryPoint = "flower_audio_bridge_destroy")]
        private static extern void Destroy(IntPtr bridge);

        [DllImport("miniaudio", EntryPoint = "flower_audio_bridge_data_callback")]
        private static extern IntPtr DataCallback();

        [DllImport("miniaudio", EntryPoint = "flower_audio_bridge_attach")]
        private static extern int Attach(IntPtr bridge, ma_device* device);

        [DllImport("miniaudio", EntryPoint = "flower_audio_bridge_detach")]
        private static extern void Detach(IntPtr bridge);

        [DllImport("miniaudio", EntryPoint = "flower_audio_bridge_capacity")]
        private static extern uint NativeCapacity(IntPtr bridge);

        [DllImport("miniaudio", EntryPoint = "flower_audio_bridge_available")]
        private static extern uint NativeAvailable(IntPtr bridge);

        [DllImport("miniaudio", EntryPoint = "flower_audio_bridge_write")]
        private static extern uint NativeWrite(IntPtr bridge, byte* data, uint byteCount);

        [DllImport("miniaudio", EntryPoint = "flower_audio_bridge_request_flush")]
        private static extern ulong NativeRequestFlush(IntPtr bridge);

        [DllImport("miniaudio", EntryPoint = "flower_audio_bridge_flush_acked")]
        private static extern ulong NativeFlushAcked(IntPtr bridge);

        [DllImport("miniaudio", EntryPoint = "flower_audio_bridge_flush_now")]
        private static extern void NativeFlushNow(IntPtr bridge);

        [DllImport("miniaudio", EntryPoint = "flower_audio_bridge_set_primed")]
        private static extern void NativeSetPrimed(IntPtr bridge, int primed);

        [DllImport("miniaudio", EntryPoint = "flower_audio_bridge_begin_fade_in")]
        private static extern void NativeBeginFadeIn(IntPtr bridge, uint fadeFrames);

        [DllImport("miniaudio", EntryPoint = "flower_audio_bridge_begin_fade_out")]
        private static extern void NativeBeginFadeOut(IntPtr bridge, uint fadeFrames);

        [DllImport("miniaudio", EntryPoint = "flower_audio_bridge_fade_out_completed")]
        private static extern int NativeFadeOutCompleted(IntPtr bridge);

        [DllImport("miniaudio", EntryPoint = "flower_audio_bridge_take_snapshot")]
        private static extern void NativeTakeSnapshot(IntPtr bridge, out NativeSnapshot snapshot);

        private static bool? _isAvailable;

        // Probed by calling the cheapest entry point there is and catching the
        // load failure, rather than inferred from OperatingSystem.IsAndroid()
        // or IsIOS(): what actually decides this is which miniaudio binary got
        // linked, and a build that vendors one on a platform this doesn't know
        // about should get the native path without a code change here.
        public static bool IsAvailable
        {
            get
            {
                if (_isAvailable is { } known)
                    return known;

                try
                {
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

        // Null when the native side could not allocate, which the caller
        // treats the same way it treats a platform without the symbols at
        // all: keep the managed callback.
        public static NativeAudioBridge? TryCreate(int capacityBytes, int bytesPerFrame)
        {
            if (!IsAvailable || capacityBytes <= 0 || bytesPerFrame <= 0)
                return null;

            var handle = Create((uint)capacityBytes, (uint)bytesPerFrame);
            return handle == IntPtr.Zero ? null : new NativeAudioBridge(handle);
        }

        // The ma_device_config.dataCallback to install in place of a managed
        // one. Typed back to the binding's own function-pointer shape here so
        // that the cast lives in exactly one place.
        public static delegate* unmanaged[Cdecl]<ma_device*, void*, void*, uint, void> RenderCallback =>
            (delegate* unmanaged[Cdecl]<ma_device*, void*, void*, uint, void>)DataCallback();

        public void AttachTo(ma_device* device) => Attach(_handle, device);

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
                (int)snapshot.MaxIdenticalCallbackRun);
        }

        public void Dispose()
        {
            if (_disposed)
                return;

            _disposed = true;
            Destroy(_handle);
        }
    }
}
