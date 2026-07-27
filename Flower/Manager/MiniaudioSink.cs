using System;
using System.IO;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Timers;

using Microsoft.Extensions.Logging;

using Miniaudio;

namespace Flower.Manager
{
    // Default IAudioSink on every platform: plays the shared GaplessRingBuffer
    // back out through a dedicated miniaudio playback device - one real-time
    // data callback pulling directly from the ring buffer. Android/iOS use
    // their own vendored native miniaudio build (native/miniaudio/, no NuGet
    // package - see CLAUDE.md). Replaces LibVlcRawStreamSink, which
    // piggybacked on LibVLC's rawaud demuxer/aout to render a synthetic,
    // never-ending PCM stream. That indirection turned out to be the source
    // of real playback bugs found during manual testing - a seek on a
    // completely unrelated decode-side MediaPlayer could freeze the render
    // MediaPlayer solid for several seconds, proven via watchdog logging
    // (render Time frozen while both decoders kept producing PCM normally).
    // miniaudio's ma_device model has no demuxer/decode state machine to
    // wedge in the first place - it only ever asks this callback for more
    // PCM, on its own real-time thread.
    public sealed unsafe class MiniaudioSink : IAudioSink
    {
        private readonly ILogger<MiniaudioSink> _logger;
        private readonly object _gate = new();
        private GaplessRingBuffer? _ringBuffer;
        private volatile Equalizer? _equalizer;
        private ma_context* _context;
        private ma_device* _device;
        private volatile bool _started;
        private bool _disposed;

        // The data callback is [UnmanagedCallersOnly] - it can't close over
        // instance state, so this instance's GCHandle is stashed in
        // ma_device_config.pUserData (copied onto ma_device.pUserData by
        // miniaudio) as the only way back from the native callback to
        // _ringBuffer. Freed in Dispose().
        private GCHandle _selfHandle;

        // Diagnostic-only: watches ring.UnderrunCount once a second so a
        // crackling/glitching report can be correlated against actual
        // buffer underruns (ring momentarily empty when the callback asked
        // for data) versus some other cause (format/rate mismatch, etc).
        // Only logs when the underrun count actually moves or the device's
        // running state changes - a healthy render loop ticks silently.
        private readonly Timer _watchdog;
        private long _watchdogLastUnderrunCount;
        private bool _watchdogLastStarted;

        public event EventHandler? Playing;
        public event EventHandler? Paused;
        public event EventHandler? Stopped;

        // NativeLibrary's default probing for a bare DllImport("miniaudio")
        // string tries flat names ("libminiaudio.dylib", "miniaudio.dylib",
        // etc.) on a handful of standard search paths - it has no notion of
        // reaching into Frameworks/miniaudio.framework/miniaudio, the
        // nested layout a NativeReference embeds an iOS framework at (see
        // Flower.iOS.csproj's NativeReference comment). Confirmed via a
        // real on-device-equivalent (simulator) run: the framework was
        // correctly embedded, signed, and even directly linked into the
        // main executable (LC_LOAD_DYLIB @rpath/miniaudio.framework/
        // miniaudio, visible via otool -L), yet the managed DllImport
        // still threw DllNotFoundException - .NET's iOS interpreter
        // resolves P/Invokes via its own dlopen-by-string lookup, which
        // never tried this path. Mirrors VlcNativeSetup.cs's Linux
        // DllImportResolver (libvlc -> libvlc.so.5) for exactly the same
        // reason: the default probing doesn't know the real on-disk name.
        static MiniaudioSink()
        {
            if (OperatingSystem.IsIOS())
                NativeLibrary.SetDllImportResolver(typeof(ma).Assembly, ResolveIosMiniaudio);
        }

        private static IntPtr ResolveIosMiniaudio(string libraryName, System.Reflection.Assembly assembly, DllImportSearchPath? searchPath)
        {
            if (libraryName != "miniaudio")
                return IntPtr.Zero;

            var path = Path.Combine(AppContext.BaseDirectory, "Frameworks", "miniaudio.framework", "miniaudio");
            return NativeLibrary.TryLoad(path, out var handle) ? handle : IntPtr.Zero;
        }

        public MiniaudioSink(ILogger<MiniaudioSink> logger)
        {
            _logger = logger;

            _watchdog = new Timer(1000);
            _watchdog.Elapsed += (_, _) => LogWatchdogTick();
            _watchdog.Start();
        }

        private void LogWatchdogTick()
        {
            var ring = _ringBuffer;
            if (ring == null)
                return;

            var underrunCount = ring.UnderrunCount;
            var started = _started;

            if (underrunCount != _watchdogLastUnderrunCount)
            {
                _logger.LogWarning(
                    "Render watchdog: underrun(s) detected - Started={Started} RingAvailable={Available}/{Capacity} Underruns={Underruns} (+{NewUnderruns})",
                    started, ring.AvailableBytes, ring.Capacity, underrunCount, underrunCount - _watchdogLastUnderrunCount);
            }
            else if (started != _watchdogLastStarted)
            {
                _logger.LogInformation(
                    "Render watchdog: device running state changed - Started={Started} RingAvailable={Available}/{Capacity} Underruns={Underruns}",
                    started, ring.AvailableBytes, ring.Capacity, underrunCount);
            }

            _watchdogLastUnderrunCount = underrunCount;
            _watchdogLastStarted = started;
        }

        public bool IsPlaying => _started;

        public int Volume
        {
            get
            {
                if (_device == null)
                    return 0;

                float volume;
                ma.device_get_master_volume(_device, &volume);
                return (int)Math.Round(volume * 100);
            }
            set
            {
                if (_device != null)
                    ma.device_set_master_volume(_device, value / 100f);
            }
        }

        public void ApplyEqualizer(Equalizer? equalizer) => _equalizer = equalizer;

        public void Start(GaplessRingBuffer ringBuffer)
        {
            lock (_gate)
            {
                _ringBuffer = ringBuffer;
                _selfHandle = GCHandle.Alloc(this);

                var config = ma.device_config_init(ma_device_type.ma_device_type_playback);
                config.playback.format = ma_format.ma_format_s16;
                config.playback.channels = GaplessFormat.Channels;
                config.sampleRate = GaplessFormat.SampleRate;
                config.dataCallback = &DataCallback;
                config.pUserData = (void*)GCHandle.ToIntPtr(_selfHandle);

                // miniaudio's default (low-latency) profile picks a very
                // small period size, tuned for tight native C callbacks.
                // This callback runs on a managed .NET thread (a GCHandle
                // lookup, a bounds-checked Span copy, all under the CLR),
                // which can occasionally take just long enough to miss that
                // tiny window - heard as crackling. Conservative trades a
                // little extra latency for a much bigger per-period safety
                // margin.
                config.performanceProfile = ma_performance_profile.ma_performance_profile_conservative;

                // ma_context's actual native size depends on which backends
                // (CoreAudio/WASAPI/ALSA/...) this specific prebuilt
                // libminiaudio was compiled with - Miniaudio-CS's C# struct
                // is generated once and shared across every platform's
                // native binary, so trusting its own sizeof(ma_context) here
                // can under-allocate on whichever platform's backend union
                // is larger than what the binding captured. ma_context_sizeof()
                // is miniaudio's own runtime escape hatch for exactly this -
                // it asks the *actual loaded native library* how big its own
                // ma_context is, so the allocation is always correctly sized
                // regardless of the C# struct's guess.
                _context = (ma_context*)NativeMemory.Alloc(ma.context_sizeof());
                var contextResult = ma.context_init(null, 0, null, _context);
                if (contextResult != ma_result.MA_SUCCESS)
                {
                    _logger.LogError("miniaudio context_init failed: {Result}", contextResult);
                    NativeMemory.Free(_context);
                    _context = null;
                    return;
                }

                // ma_device has the same cross-platform-union problem as
                // ma_context (see above) but miniaudio doesn't expose an
                // ma_device_sizeof() equivalent to ask for its real size, so
                // sizeof(ma_device) here is the C# binding's guess, not a
                // guarantee. Padding the allocation well past that guess is
                // cheap insurance against ma_device_init writing past the
                // end of an under-sized block and corrupting whatever heap
                // allocation happens to sit right after it - which is
                // exactly what a real run of this code did on first boot,
                // surfaced by macOS's malloc as a delayed, unrelated-looking
                // "Incorrect checksum for freed object" crash.
                const int deviceAllocationPadding = 4096;
                _device = (ma_device*)NativeMemory.Alloc((nuint)sizeof(ma_device) + deviceAllocationPadding);
                var result = ma.device_init(_context, &config, _device);
                if (result != ma_result.MA_SUCCESS)
                {
                    _logger.LogError("miniaudio device_init failed: {Result}", result);
                    NativeMemory.Free(_device);
                    _device = null;
                    ma.context_uninit(_context);
                    NativeMemory.Free(_context);
                    _context = null;
                    return;
                }

                _logger.LogInformation("miniaudio playback device initialized");
            }
        }

        [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
        private static void DataCallback(ma_device* pDevice, void* pOutput, void* pInput, uint frameCount)
        {
            var handle = GCHandle.FromIntPtr((IntPtr)pDevice->pUserData);
            if (handle.Target is not MiniaudioSink sink || sink._ringBuffer is not { } ring)
                return;

            var byteCount = checked((int)frameCount * GaplessFormat.BytesPerFrame);
            var dest = new Span<byte>(pOutput, byteCount);

            // Read() never blocks - a short/zero read just means the ring is
            // temporarily empty (decode running behind), not end-of-stream.
            // This callback runs on miniaudio's real-time thread, so the
            // remainder of the requested frames is silence-padded rather
            // than waited for.
            var read = ring.Read(dest);
            if (read < byteCount)
                dest[read..].Clear();

            // null = true bypass - skip the processing call entirely rather
            // than running an all-zero-dB filter. Only the bytes actually
            // filled with real audio are processed, never the silence-padded
            // tail from a short read above.
            if (sink._equalizer is { } equalizer)
                equalizer.ProcessInPlace(dest[..read]);
        }

        public void Resume()
        {
            lock (_gate)
            {
                if (_device == null || _started)
                    return;

                var result = ma.device_start(_device);
                if (result != ma_result.MA_SUCCESS)
                {
                    _logger.LogWarning("miniaudio device_start failed: {Result}", result);
                    return;
                }

                _started = true;
                Playing?.Invoke(this, EventArgs.Empty);
            }
        }

        public void Pause()
        {
            lock (_gate)
            {
                if (_device == null || !_started)
                    return;

                ma.device_stop(_device);
                _started = false;
                Paused?.Invoke(this, EventArgs.Empty);
            }
        }

        public void Stop()
        {
            lock (_gate)
            {
                if (_device == null || !_started)
                    return;

                ma.device_stop(_device);
                _started = false;
                Stopped?.Invoke(this, EventArgs.Empty);
            }
        }

        public void Dispose()
        {
            lock (_gate)
            {
                if (_disposed)
                    return;
                _disposed = true;

                _watchdog.Dispose();

                if (_device != null)
                {
                    ma.device_uninit(_device);
                    NativeMemory.Free(_device);
                    _device = null;
                }

                if (_context != null)
                {
                    ma.context_uninit(_context);
                    NativeMemory.Free(_context);
                    _context = null;
                }

                if (_selfHandle.IsAllocated)
                    _selfHandle.Free();
            }
        }
    }
}
