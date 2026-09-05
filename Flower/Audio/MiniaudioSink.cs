using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Timers;

using Avalonia.Threading;

using Timer = System.Timers.Timer;

using Microsoft.Extensions.Logging;

using Miniaudio;

namespace Flower.Audio
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
        // How long the prime latch will wait for PrebufferMs of audio before
        // giving up and rendering whatever there is. Not a setting: it is not
        // a quality knob but a safety net for a decoder that is never going to
        // deliver (an unreachable stream, a file that failed to open), where
        // the right answer is to stop holding the output hostage.
        private const int PrimeDeadlineMs = 1500;

        private readonly ILogger<MiniaudioSink> _logger;
        private readonly object _gate = new();
        private GaplessRingBuffer? _ringBuffer;

        // Non-null only where a native miniaudio build carrying
        // flower_audio_bridge is what got loaded - Android and iOS. There the
        // render callback is pure C draining _bridge, and _feeder is the
        // managed thread that fills it; DataCallback below is dead code on
        // those platforms. Everywhere else both stay null and the managed
        // callback runs as it always has. See NativeAudioBridge for why the
        // split falls where it does.
        private NativeAudioBridge? _bridge;
        private AudioFeeder? _feeder;

        // Everything between the ring and the sound card - float widening, EQ,
        // gain ramping, declick envelope, dithered requantisation. Owned here
        // and driven from DataCallback; see OutputStage for why none of that
        // can be left to miniaudio.
        private OutputStage _outputStage = new(GaplessFormat.DefaultSampleRate);

        // Prime latch: after a flush the ring is empty and the decoder is
        // still opening the file, so the callback would render a trickle of
        // starved fragments interleaved with silence for the whole media-open
        // latency. While unprimed it renders silence instead - not counted as
        // an underrun, because nothing is wrong - until either the ring holds
        // PrebufferMs of audio or the deadline passes. Callback-owned except
        // _primeDeadline, which is only read there.
        private bool _primed;
        private int _primeGeneration = -1;
        private long _primeDeadlineTimestamp;
        private ma_context* _context;
        private ma_device* _device;
        private volatile bool _started;
        private bool _disposed;

        // The first opened device establishes the canonical PCM rate for the
        // lifetime of this sink. An output-device change may need miniaudio to
        // resample, but changing the rate while current and armed decoders
        // exist would corrupt timing and playback speed.
        private bool _hasNegotiatedFormat;

        // The output device the user explicitly picked (an opaque base64
        // ma_device_id - see EncodeDeviceId), or null while Flower just
        // follows whatever the OS calls the default. Kept as the encoded
        // string rather than a decoded ma_device_id because it is only ever
        // needed to reopen the device, and a string needs no native
        // allocation to stay alive between reopens.
        private string? _outputDeviceId;

        // The device Flower is actually rendering to right now, as an encoded
        // id - which is _outputDeviceId when the user picked one, and the id
        // of whatever was flagged default at open time when they did not.
        // Kept so a reroute can be told apart from a disappearance: on a
        // reroute miniaudio has already moved us somewhere new, and the only
        // way to know whether that was a device vanishing or the user changing
        // their OS default is whether this one is still in the device list.
        //
        // Read out of a fresh enumeration rather than out of ma_device's own
        // playback.id: this class already refuses to trust the binding's
        // ma_device layout (see OpenDevice's padding comment), and
        // context_get_devices is the one shape MiniaudioBindingLayoutTests
        // actually pins.
        private string? _activeDeviceId;

        // Non-zero while Flower is itself stopping or tearing down the device,
        // so the ma_device_notification_type_stopped miniaudio raises from
        // inside device_stop/device_uninit is recognised as our own doing
        // rather than the output dying. Only ever changed under _gate, and
        // always read from the notification callback - which, for an
        // intentional stop, is invoked synchronously on the very thread
        // holding the lock.
        private volatile int _intentionalStopDepth;

        // The user's 0-100 volume, kept here rather than read back out of
        // ma_device: it is applied by OutputStage now, not by miniaudio, whose
        // own master volume is pinned at 1.0 (its volume path is a truncating,
        // undithered integer multiply - see OutputStage).
        private volatile int _volumePercent = 100;

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
        private long _watchdogLastShortReadCount;
        private long _watchdogLastCallbackExceptionCount;
        private bool _watchdogLastStarted;
        private long _watchdogLastRealBytesRendered;
        private int _watchdogNoProgressTicks;
        private int _watchdogTickCount;

        // Written by the real-time callback and only read/reset by the
        // watchdog. Keep diagnostics here to counters and a sampled hash;
        // formatting or logging from the audio thread itself could cause the
        // very underruns these fields exist to diagnose.
        private long _callbackCount;
        private long _requestedBytes;
        private long _realBytesRendered;
        private long _silenceBytesRendered;
        private long _shortReadCount;
        private long _callbackExceptionCount;
        private long _lastPcmFingerprint;
        private long _lastCallbackFingerprint;
        private int _lastCallbackReadBytes;
        private int _consecutiveIdenticalCallbacks;
        private int _maxIdenticalCallbackRun;
        private readonly AudioCallbackTiming _callbackTiming = new();

        // The managed callback tells us whether Flower supplied PCM on time.
        // These native counters fill in the next boundary on iOS: CoreAudio's
        // callback cadence, its host timestamps, and whether every requested
        // frame was actually handed to miniaudio. They are sampled from the
        // watchdog rather than logged from the real-time callback.
        [StructLayout(LayoutKind.Sequential)]
        private struct CoreAudioDiagnosticsSnapshot
        {
            public ulong CallbackCount;
            public ulong RequestedFrames;
            public ulong SubmittedFrames;
            public ulong ActionFlags;
            public ulong MaxCallbackGapNanoseconds;
            public ulong MaxHostTimeGapNanoseconds;
            public ulong MaxCallbackDurationNanoseconds;
            public ulong MaxSampleDelta;
            public ulong AbruptFrameCount;
            public ulong RepeatedBufferCount;
            public uint MinFrames;
            public uint MaxFrames;
            public uint MaxActionFlags;
            public uint MaxRepeatedBufferRun;

            public double MaxCallbackGapMilliseconds => MaxCallbackGapNanoseconds / 1_000_000.0;
            public double MaxHostTimeGapMilliseconds => MaxHostTimeGapNanoseconds / 1_000_000.0;
            public double MaxCallbackDurationMilliseconds => MaxCallbackDurationNanoseconds / 1_000_000.0;
        }

        public event EventHandler? Playing;
        public event EventHandler? Paused;
        public event EventHandler? Stopped;
        public event EventHandler? OutputDeviceLost;

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

            // In bridge mode the counters live in C, on the far side of the
            // hand-off. Folding them into the same fields the managed callback
            // would have written keeps every line below identical on both
            // paths - the numbers mean the same thing either way, they are
            // just gathered by whoever is doing the rendering.
            FoldBridgeCounters();

            var underrunCount = ring.UnderrunCount;
            var started = _started;
            var realBytesRendered = Interlocked.Read(ref _realBytesRendered);
            var shortReadCount = Interlocked.Read(ref _shortReadCount);
            var newShortReads = shortReadCount - Interlocked.Read(ref _watchdogLastShortReadCount);
            Interlocked.Exchange(ref _watchdogLastShortReadCount, shortReadCount);
            var callbackExceptionCount = Interlocked.Read(ref _callbackExceptionCount);
            var newCallbackExceptions = callbackExceptionCount - Interlocked.Read(ref _watchdogLastCallbackExceptionCount);
            Interlocked.Exchange(ref _watchdogLastCallbackExceptionCount, callbackExceptionCount);
            var maxIdenticalRun = Interlocked.Exchange(ref _maxIdenticalCallbackRun, 0);
            var callbackTiming = _callbackTiming.TakeSnapshot();
            var coreAudioDiagnostics = TakeCoreAudioDiagnostics();

            if (started && realBytesRendered == _watchdogLastRealBytesRendered)
                _watchdogNoProgressTicks++;
            else
                _watchdogNoProgressTicks = 0;

            if (underrunCount != _watchdogLastUnderrunCount)
            {
                _logger.LogWarning(
                    "Render watchdog: underrun(s) detected - Started={Started} RingAvailable={Available}/{Capacity} Underruns={Underruns} (+{NewUnderruns})",
                    started, ring.AvailableBytes, ring.Capacity, underrunCount, underrunCount - _watchdogLastUnderrunCount);
            }
            else if (newCallbackExceptions > 0)
            {
                _logger.LogError(
                    "Render watchdog: managed callback exception(s) were silenced - CallbackExceptions={CallbackExceptions} (+{NewCallbackExceptions}) RingAvailable={Available}/{Capacity} RingGeneration={RingGeneration}",
                    callbackExceptionCount, newCallbackExceptions, ring.AvailableBytes,
                    ring.Capacity, ring.Generation);
            }
            else if (newShortReads > 0)
            {
                _logger.LogWarning(
                    "Render watchdog: partial buffer starvation detected - ShortReads={ShortReads} (+{NewShortReads}) RealBytes={RealBytes} SilenceBytes={SilenceBytes} RingAvailable={Available}/{Capacity} RingRead={RingRead} RingWritten={RingWritten} RingGeneration={RingGeneration}",
                    shortReadCount, newShortReads, realBytesRendered,
                    Interlocked.Read(ref _silenceBytesRendered), ring.AvailableBytes,
                    ring.Capacity, ring.TotalBytesRead, ring.TotalBytesWritten,
                    ring.Generation);
            }
            else if (started != _watchdogLastStarted)
            {
                _logger.LogInformation(
                    "Render watchdog: device running state changed - Started={Started} RingAvailable={Available}/{Capacity} Underruns={Underruns}",
                    started, ring.AvailableBytes, ring.Capacity, underrunCount);
            }

            if (_watchdogNoProgressTicks == 2)
            {
                _logger.LogWarning(
                    "Render watchdog: device is started but consumed no real PCM for 2s - Callbacks={Callbacks} RequestedBytes={RequestedBytes} RealBytes={RealBytes} SilenceBytes={SilenceBytes} RingAvailable={Available}/{Capacity} RingRead={RingRead} RingWritten={RingWritten} RingGeneration={RingGeneration}",
                    Interlocked.Read(ref _callbackCount), Interlocked.Read(ref _requestedBytes),
                    realBytesRendered, Interlocked.Read(ref _silenceBytesRendered),
                    ring.AvailableBytes, ring.Capacity, ring.TotalBytesRead,
                    ring.TotalBytesWritten, ring.Generation);
            }

            if (maxIdenticalRun >= 50)
            {
                _logger.LogWarning(
                    "Render watchdog: {RepeatedCallbacks} consecutive audio callbacks carried identical PCM; possible static or repeated buffer - LastReadBytes={LastReadBytes} PcmFingerprint={PcmFingerprint} RingAvailable={Available}/{Capacity} RingGeneration={RingGeneration}",
                    maxIdenticalRun, Volatile.Read(ref _lastCallbackReadBytes),
                    Interlocked.Read(ref _lastPcmFingerprint), ring.AvailableBytes,
                    ring.Capacity, ring.Generation);
            }

            if (callbackTiming.InterestingGaps > 0 || callbackTiming.RenderOverruns > 0)
            {
                _logger.LogWarning(
                    "Render watchdog: callback timing pressure detected - InterestingGaps={InterestingGaps} RenderOverruns={RenderOverruns} Callbacks={Callbacks} TotalFrames={TotalFrames} AverageFrames={AverageFrames:F1} MaxGapMs={MaxGapMs:F2} ExpectedPeriodMs={ExpectedPeriodMs:F2} MaxLateMs={MaxLateMs:F2} MaxRenderMs={MaxRenderMs:F2} Frames={MinFrames}-{MaxFrames} MaxGapFrames={PrecedingFrames}->{CurrentFrames}",
                    callbackTiming.InterestingGaps, callbackTiming.RenderOverruns,
                    callbackTiming.CallbackCount, callbackTiming.TotalFrames, callbackTiming.AverageFramesPerCallback,
                    callbackTiming.MaxGapMilliseconds, callbackTiming.ExpectedPeriodMilliseconds,
                    callbackTiming.MaxLateMilliseconds, callbackTiming.MaxRenderMilliseconds,
                    callbackTiming.MinFrames, callbackTiming.MaxFrames,
                    callbackTiming.PrecedingFramesAtMaxGap, callbackTiming.CurrentFramesAtMaxGap);
            }

            if (coreAudioDiagnostics is { } coreAudioPressure
                && (coreAudioPressure.MaxCallbackGapMilliseconds >= 60
                    || coreAudioPressure.ActionFlags != 0
                    || coreAudioPressure.RequestedFrames != coreAudioPressure.SubmittedFrames
                    || coreAudioPressure.AbruptFrameCount != 0
                    || coreAudioPressure.RepeatedBufferCount != 0))
            {
                _logger.LogWarning(
                    "CoreAudio render diagnostics: Callbacks={Callbacks} RequestedFrames={RequestedFrames} SubmittedFrames={SubmittedFrames} MaxCallbackGapMs={MaxCallbackGapMs:F2} MaxHostTimeGapMs={MaxHostTimeGapMs:F2} MaxCallbackDurationMs={MaxCallbackDurationMs:F2} Frames={MinFrames}-{MaxFrames} ActionFlags=0x{ActionFlags:X} MaxSampleDelta={MaxSampleDelta} AbruptFrames={AbruptFrames} RepeatedBuffers={RepeatedBuffers} MaxRepeatedBufferRun={MaxRepeatedBufferRun}",
                    coreAudioPressure.CallbackCount, coreAudioPressure.RequestedFrames, coreAudioPressure.SubmittedFrames,
                    coreAudioPressure.MaxCallbackGapMilliseconds, coreAudioPressure.MaxHostTimeGapMilliseconds,
                    coreAudioPressure.MaxCallbackDurationMilliseconds, coreAudioPressure.MinFrames, coreAudioPressure.MaxFrames,
                    coreAudioPressure.ActionFlags, coreAudioPressure.MaxSampleDelta, coreAudioPressure.AbruptFrameCount,
                    coreAudioPressure.RepeatedBufferCount, coreAudioPressure.MaxRepeatedBufferRun);
            }

            _watchdogTickCount++;
            if (_watchdogTickCount % 10 == 0 && started)
            {
                _logger.LogDebug(
                    "Render snapshot: Started={Started} Callbacks={Callbacks} TimingCallbacks={TimingCallbacks} TimingTotalFrames={TimingTotalFrames} TimingAverageFrames={TimingAverageFrames:F1} RequestedBytes={RequestedBytes} RealBytes={RealBytes} SilenceBytes={SilenceBytes} ShortReads={ShortReads} Underruns={Underruns} RingRead={RingRead} RingWritten={RingWritten} RingAvailable={Available}/{Capacity} RingGeneration={RingGeneration} PcmFingerprint={PcmFingerprint} MaxGapMs={MaxGapMs:F2} ExpectedPeriodMs={ExpectedPeriodMs:F2} MaxLateMs={MaxLateMs:F2} MaxRenderMs={MaxRenderMs:F2} Frames={MinFrames}-{MaxFrames} MaxGapFrames={PrecedingFrames}->{CurrentFrames}",
                    started, Interlocked.Read(ref _callbackCount), callbackTiming.CallbackCount,
                    callbackTiming.TotalFrames, callbackTiming.AverageFramesPerCallback,
                    Interlocked.Read(ref _requestedBytes), realBytesRendered,
                    Interlocked.Read(ref _silenceBytesRendered), shortReadCount,
                    underrunCount, ring.TotalBytesRead, ring.TotalBytesWritten,
                    ring.AvailableBytes, ring.Capacity, ring.Generation,
                    Interlocked.Read(ref _lastPcmFingerprint), callbackTiming.MaxGapMilliseconds,
                    callbackTiming.ExpectedPeriodMilliseconds, callbackTiming.MaxLateMilliseconds,
                    callbackTiming.MaxRenderMilliseconds, callbackTiming.MinFrames,
                    callbackTiming.MaxFrames, callbackTiming.PrecedingFramesAtMaxGap,
                    callbackTiming.CurrentFramesAtMaxGap);

                if (coreAudioDiagnostics is { } coreAudioSnapshot)
                {
                    _logger.LogDebug(
                        "CoreAudio render snapshot: Callbacks={Callbacks} RequestedFrames={RequestedFrames} SubmittedFrames={SubmittedFrames} MaxCallbackGapMs={MaxCallbackGapMs:F2} MaxHostTimeGapMs={MaxHostTimeGapMs:F2} MaxCallbackDurationMs={MaxCallbackDurationMs:F2} Frames={MinFrames}-{MaxFrames} ActionFlags=0x{ActionFlags:X} MaxSampleDelta={MaxSampleDelta} AbruptFrames={AbruptFrames} RepeatedBuffers={RepeatedBuffers} MaxRepeatedBufferRun={MaxRepeatedBufferRun}",
                        coreAudioSnapshot.CallbackCount, coreAudioSnapshot.RequestedFrames, coreAudioSnapshot.SubmittedFrames,
                        coreAudioSnapshot.MaxCallbackGapMilliseconds, coreAudioSnapshot.MaxHostTimeGapMilliseconds,
                        coreAudioSnapshot.MaxCallbackDurationMilliseconds, coreAudioSnapshot.MinFrames, coreAudioSnapshot.MaxFrames,
                        coreAudioSnapshot.ActionFlags, coreAudioSnapshot.MaxSampleDelta, coreAudioSnapshot.AbruptFrameCount,
                        coreAudioSnapshot.RepeatedBufferCount, coreAudioSnapshot.MaxRepeatedBufferRun);
                }
            }

            _watchdogLastUnderrunCount = underrunCount;
            _watchdogLastStarted = started;
            _watchdogLastRealBytesRendered = realBytesRendered;
        }

        // Reading the native snapshot resets it, so this must happen exactly
        // once per watchdog tick and nowhere else.
        private void FoldBridgeCounters()
        {
            if (_bridge is not { } bridge)
                return;

            var snapshot = bridge.TakeSnapshot();
            Interlocked.Add(ref _callbackCount, snapshot.CallbackCount);
            Interlocked.Add(ref _requestedBytes, snapshot.RequestedBytes);
            Interlocked.Add(ref _realBytesRendered, snapshot.RealBytes);
            Interlocked.Add(ref _silenceBytesRendered, snapshot.SilenceBytes);
            Interlocked.Add(ref _shortReadCount, snapshot.ShortReadCount);
            Interlocked.Exchange(ref _lastPcmFingerprint, snapshot.LastPcmFingerprint);

            // Folded like the rest, and it was the one field that was not:
            // _lastCallbackReadBytes is written only by the managed render
            // callback, which is dead code whenever the bridge is doing the
            // rendering. So every identical-PCM warning off a phone reported
            // LastReadBytes=0 - not a zero read, just a field nobody had
            // written - which reads as "the sink read nothing from a ring that
            // had data in it" and is a different bug entirely.
            Volatile.Write(ref _lastCallbackReadBytes, (int)snapshot.LastReadBytes);
            if (snapshot.MaxIdenticalCallbackRun > Volatile.Read(ref _maxIdenticalCallbackRun))
                Volatile.Write(ref _maxIdenticalCallbackRun, snapshot.MaxIdenticalCallbackRun);
        }

        public bool IsPlaying => _started;

        public int Volume
        {
            get => _volumePercent;

            // No lock and no device call: this is a single volatile store the
            // render callback picks up on its next pass and ramps toward over
            // GainRampMs. Taking _gate here used to put the UI thread behind
            // whatever else held it, for a value nothing but the callback
            // reads.
            set
            {
                var percent = Math.Clamp(value, 0, 100);
                _volumePercent = percent;
                _outputStage.TargetGain = OutputStage.GainForVolumePercent(percent);
            }
        }

        public void ApplyEqualizer(Equalizer? equalizer) => _outputStage.Equalizer = equalizer;

        public void ApplyTiming(AudioTimingSettings timing) => _outputStage.Timing = timing;

        public void Start(GaplessRingBuffer ringBuffer)
        {
            lock (_gate)
            {
                _ringBuffer = ringBuffer;
                _selfHandle = GCHandle.Alloc(this);

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
                // On iOS miniaudio otherwise owns AVAudioSession as a side
                // effect of opening its context. Its default configuration is
                // PlayAndRecord + DefaultToSpeaker, which can select the
                // handset speaker before Flower has a chance to configure its
                // music session for an AirPods/AirPlay route. It also activates
                // that session at app startup, even though no track is playing.
                //
                // AppleAudioSession is deliberately the one owner there: it
                // selects Playback + AllowBluetoothA2DP immediately before
                // rendering and releases the session on pause/stop. Asking
                // miniaudio to leave the shared session alone avoids the two
                // layers silently overwriting each other's category, options,
                // activation state, and ultimately route.
                var contextConfig = ma.context_config_init();
                ConfigureContextForPlatform(ref contextConfig, OperatingSystem.IsIOS());

                var contextResult = ma.context_init(null, 0, &contextConfig, _context);
                if (contextResult != ma_result.MA_SUCCESS)
                {
                    _logger.LogError("miniaudio context_init failed: {Result}", contextResult);
                    NativeMemory.Free(_context);
                    _context = null;
                    return;
                }

                if (!OpenDevice())
                {
                    ma.context_uninit(_context);
                    NativeMemory.Free(_context);
                    _context = null;
                    return;
                }

                _logger.LogInformation("miniaudio playback device initialized");
            }
        }

        // Kept separate from Start so the iOS session-ownership contract can
        // be regression-tested without loading an audio device or requiring
        // AirPods on the test machine.
        internal static void ConfigureContextForPlatform(ref ma_context_config contextConfig, bool isIos)
        {
            if (!isIos)
                return;

            contextConfig.coreaudio.sessionCategory = ma_ios_session_category.ma_ios_session_category_none;
            contextConfig.coreaudio.noAudioSessionActivate = 1;
            contextConfig.coreaudio.noAudioSessionDeactivate = 1;
        }

        // Opens the playback device that _outputDeviceId currently names (or
        // the OS default when it is null), leaving _device valid but stopped
        // on success and null on failure. Split out of Start because
        // SetOutputDevice has to do exactly the same thing again: miniaudio
        // has no "move this device to that endpoint" call, so changing output
        // means uninit-ing the ma_device and initialising a new one against
        // the chosen ma_device_id. The caller holds _gate and has already
        // initialised _context.
        private bool OpenDevice()
        {
            var config = ma.device_config_init(ma_device_type.ma_device_type_playback);
            config.playback.format = MiniaudioFormatFor(GaplessFormat.SampleFormat);
            config.playback.channels = GaplessFormat.Channels;
            // During the first open, zero asks miniaudio for the endpoint's
            // native rate. The decoder is configured from that result before
            // playback begins, leaving LibVLC as the only sample-rate
            // converter in the chain. Keep that rate on later device changes:
            // existing current and armed decoders cannot safely change format.
            config.sampleRate = _hasNegotiatedFormat ? GaplessFormat.SampleRate : 0;
            // The bridge's callback is a native symbol in the same library
            // miniaudio itself came from, so installing it costs nothing and
            // needs no managed thunk. It renders silence until AttachTo binds
            // a bridge to this device below, which happens before anything
            // can start the device.
            //
            // No format gate any more. This used to be S16-only, because
            // flower_audio_bridge's transport fade walked its buffer as
            // int16_t*, and handing that packed 24-bit PCM would not merely
            // skip the fade - it would rewrite every sample as though
            // three-byte frames were two-byte ones. So electing FFmpeg on a
            // phone bought 24 bits by giving up the one thing the bridge
            // exists for, a render callback Mono's GC cannot suspend, on
            // exactly the two platforms where that matters. The envelope now
            // takes the width (flower_audio_bridge_create's bytesPerSample),
            // so neither has to be traded for the other.
            var useBridge = NativeAudioBridge.IsAvailable
                && _outputStage.Timing.NativeBufferMs > 0;

            if (useBridge)
                config.dataCallback = NativeAudioBridge.RenderCallback;
            else
                config.dataCallback = &DataCallback;
            config.notificationCallback = &NotificationCallback;
            config.pUserData = (void*)GCHandle.ToIntPtr(_selfHandle);

            // miniaudio's default (low-latency) profile picks a very small
            // period size, tuned for tight native C callbacks. Conservative
            // trades a little extra latency for a much bigger per-period
            // safety margin, which is what the managed callback below needs -
            // a GCHandle lookup and a bounds-checked Span copy, all under the
            // CLR, can take just long enough to miss a tiny window.
            //
            // It is not enough on its own, and was never the real answer on
            // mobile: what actually stalls the render thread there is Mono
            // suspending it for a GC, for hundreds of milliseconds, which no
            // period size survives. That is what the bridge below exists for.
            config.performanceProfile = ma_performance_profile.ma_performance_profile_conservative;

            // Only read by device_init, so a stack local is enough - nothing
            // holds on to pDeviceID afterwards. Leaving it null is what asks
            // miniaudio for the OS default device.
            ma_device_id deviceId;
            if (_outputDeviceId is { } encoded)
            {
                if (TryDecodeDeviceId(encoded, &deviceId))
                {
                    config.playback.pDeviceID = &deviceId;
                }
                else
                {
                    _logger.LogWarning("Unusable output device id, falling back to the system default");
                    _outputDeviceId = null;
                }
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
            var device = (ma_device*)NativeMemory.Alloc((nuint)sizeof(ma_device) + deviceAllocationPadding);
            var result = ma.device_init(_context, &config, device);

            // The one place the device gets a say in the sample format. It is
            // asked for whatever the elected decoder can deliver (see
            // DecoderElection), and a refusal here is what says the pipeline
            // has to narrow back to S16 - which is a real answer rather than a
            // failure, because every decoder can produce S16 and every device
            // takes it.
            //
            // Only on the first open, and only downward. Later reopens keep
            // the negotiated format for the same reason they keep the
            // negotiated rate: current and armed decoders are already running
            // and cannot change format underneath the ring they share.
            if (result != ma_result.MA_SUCCESS
                && !_hasNegotiatedFormat
                && GaplessFormat.SampleFormat != PcmSampleFormat.S16)
            {
                _logger.LogWarning(
                    "miniaudio refused a {Format} playback device ({Result}); falling back to 16-bit",
                    GaplessFormat.SampleFormat, result);
                GaplessFormat.ConfigureSampleFormat(PcmSampleFormat.S16);
                config.playback.format = MiniaudioFormatFor(PcmSampleFormat.S16);

                result = ma.device_init(_context, &config, device);
            }

            if (result != ma_result.MA_SUCCESS)
            {
                _logger.LogError("miniaudio device_init failed: {Result}", result);
                NativeMemory.Free(device);
                return false;
            }

            _device = device;
            RegisterCoreAudioDiagnostics();

            if (!_hasNegotiatedFormat)
            {
                var nativeSampleRate = device->sampleRate;
                if (nativeSampleRate == 0)
                    _logger.LogWarning("miniaudio did not report a device sample rate; retaining {SampleRate}Hz", GaplessFormat.SampleRate);
                else
                    GaplessFormat.ConfigureSampleRate(nativeSampleRate);

                // Rebuilt only when it would actually be a different stage.
                // Replacing it drops the EQ curve, the timing and the gain the
                // caller set on it, which are re-applied right after
                // IAudioManager construction and not again - so an
                // unconditional replacement here would be a silent way to lose
                // them on any path that reached this block without changing
                // anything. OpenDevice only runs while the output callback is
                // stopped, so a replacement cannot race the render path.
                if (_outputStage.SampleRate != GaplessFormat.SampleRate
                    || _outputStage.SampleFormat != GaplessFormat.SampleFormat)
                {
                    _outputStage = new OutputStage(GaplessFormat.SampleRate, GaplessFormat.SampleFormat);
                }

                _logger.LogInformation(
                    "Rendering the output device's native {SampleRate}Hz at {Format}",
                    GaplessFormat.SampleRate, GaplessFormat.SampleFormat);

                _hasNegotiatedFormat = true;
            }

            // Pinned at unity, deliberately: miniaudio applies its master
            // volume after the data callback as
            // `(ma_int16)(sample * factor)` - a truncating, unrounded,
            // undithered integer multiply. OutputStage does the gain in float
            // before requantising, so this path must never be used.
            ma.device_set_master_volume(_device, 1f);

            // After the sample-rate negotiation above, so the buffer is sized
            // in the rate actually being rendered rather than the assumed one.
            if (useBridge && _ringBuffer is { } ringBuffer)
            {
                var capacity = _outputStage.Timing.NativeBufferMs * (long)GaplessFormat.SampleRate
                    * GaplessFormat.BytesPerFrame / 1000;
                _bridge = NativeAudioBridge.TryCreate(
                    (int)capacity, GaplessFormat.BytesPerFrame, GaplessFormat.BytesPerSample);
                if (_bridge == null)
                {
                    // Nothing to fall back to: the device was initialised with
                    // the native callback, which without a bridge renders
                    // silence. Reopening with the managed one is a bigger
                    // hammer than a failed malloc of a third of a second of
                    // PCM warrants, and it has never been seen to happen.
                    _logger.LogError("Could not allocate the native audio bridge; playback will be silent");
                }
                else
                {
                    _bridge.AttachTo(_device);
                    _feeder = new AudioFeeder(ringBuffer, _bridge, _outputStage);
                    _feeder.Start();
                    _logger.LogInformation(
                        "Rendering through the native audio bridge: {BufferMs}ms ({Capacity} bytes) ahead of the device",
                        _outputStage.Timing.NativeBufferMs, _bridge.Capacity);
                }
            }

            _activeDeviceId = ResolveActiveDeviceId(_outputDeviceId, GetOutputDevices());
            return true;
        }

        // The canonical format in miniaudio's own terms. ma_format_s24 is
        // packed three-byte little-endian, the same layout PcmSampleFormat.S24
        // names and flower-ffmpeg's pack_s24 writes, so nothing converts
        // between the ring and the device buffer.
        internal static ma_format MiniaudioFormatFor(PcmSampleFormat format) =>
            format == PcmSampleFormat.S24 ? ma_format.ma_format_s24 : ma_format.ma_format_s16;

        // What OpenDevice just ended up on: the explicit pick if there was
        // one, otherwise whichever device the OS currently calls default -
        // which is exactly the one miniaudio opens for a null pDeviceID.
        // Null when the list came back empty, in which case nothing can be
        // concluded from a later reroute either.
        private static string? ResolveActiveDeviceId(string? requested, IReadOnlyList<AudioOutputDevice> devices)
        {
            if (requested != null)
                return requested;

            foreach (var device in devices)
            {
                if (device.IsSystemDefault)
                    return device.Id;
            }

            return null;
        }

        // Tears down whatever device is open, if any. device_uninit stops a
        // running device on its way out, so _started stops being true here
        // without a Stopped event - a device swap is not a pause, and the UI
        // must not see one.
        private void CloseDevice()
        {
            if (_device == null)
                return;

            // Stopped before device_uninit: the feeder thread must not be
            // mid-write into a bridge that is about to be detached, and the
            // callback must not be mid-read from one about to be freed.
            _feeder?.Dispose();
            _feeder = null;

            _intentionalStopDepth++;
            try
            {
                UnregisterCoreAudioDiagnostics();
                ma.device_uninit(_device);
            }
            finally
            {
                _intentionalStopDepth--;
            }

            _bridge?.DetachFromDevice();
            _bridge?.Dispose();
            _bridge = null;

            NativeMemory.Free(_device);
            _device = null;
            _started = false;
            _callbackTiming.Reset();
            _activeDeviceId = null;
        }

        // device_stop makes miniaudio raise ma_device_notification_type_stopped,
        // synchronously and on this thread. Everything Flower stops on purpose
        // goes through here so NotificationCallback can tell that apart from
        // the output disappearing underneath us, which arrives as the same
        // notification. The caller holds _gate.
        private void StopDeviceIntentionally()
        {
            _intentionalStopDepth++;
            try
            {
                ma.device_stop(_device);
            }
            finally
            {
                _intentionalStopDepth--;
            }
        }

        // miniaudio's own out-of-band channel for "something happened to this
        // device that you did not ask for". Two of the six types matter:
        //
        //  - stopped, when Flower did not do the stopping: the endpoint went
        //    away outright, which is what pulling a USB interface or an
        //    explicitly-picked Bluetooth speaker switching off looks like.
        //  - rerouted, when the backend has already moved us to a different
        //    endpoint. Ambiguous on its own - it is equally what happens when
        //    headphones are unplugged and when the user changes their default
        //    output in Sound settings - so HandleReroute has to look before it
        //    decides.
        //
        // Runs on whatever thread the backend chose, and for an intentional
        // stop on the very thread already holding _gate, so it must not take
        // the lock or do anything slow. Both handlers are therefore queued.
        [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
        private static void NotificationCallback(ma_device_notification* notification)
        {
            if (notification == null || notification->pDevice == null)
                return;

            var handle = GCHandle.FromIntPtr((IntPtr)notification->pDevice->pUserData);
            if (handle.Target is not MiniaudioSink sink)
                return;

            switch (notification->type)
            {
                case ma_device_notification_type.ma_device_notification_type_stopped
                    when sink._intentionalStopDepth == 0:
                    ThreadPool.UnsafeQueueUserWorkItem(_ => sink.HandleUnexpectedStop(), null);
                    break;

                case ma_device_notification_type.ma_device_notification_type_rerouted:
                    ThreadPool.UnsafeQueueUserWorkItem(_ => sink.HandleReroute(), null);
                    break;
            }
        }

        // The device stopped without being asked to. There is nothing
        // ambiguous about this one: whatever Flower was rendering to is gone.
        private void HandleUnexpectedStop()
        {
            bool reopened;
            lock (_gate)
            {
                if (_disposed || _device == null || !_started)
                    return;

                _logger.LogInformation("Output device stopped unexpectedly; reopening against the system default");

                // Reopen before saying anything, so that by the time the pause
                // lands there is a working device for the user to resume onto.
                // An explicit pick that no longer enumerates is dropped rather
                // than retried - the picker should show "System default" again,
                // because that is where the sound would now come from.
                if (_outputDeviceId != null && !EnumeratedIds().Contains(_outputDeviceId))
                    _outputDeviceId = null;

                CloseDevice();
                reopened = OpenDevice();

                // CloseDevice cleared this on its way through. Restoring it is
                // what lets GaplessAudioManager's pause below run its normal
                // course - Pause() checks IsPlaying first, and the UI still
                // believes playback is running, because as far as it knows it
                // is.
                _started = true;

                if (!reopened)
                {
                    // Nothing opened at all, so there is nothing to pause onto
                    // and no later Resume can succeed. Same contract as
                    // SetOutputDevice's failed-reopen path: tell the UI it has
                    // stopped outright.
                    _started = false;
                    Stopped?.Invoke(this, EventArgs.Empty);
                    return;
                }
            }

            RaiseOutputDeviceLost();
        }

        // The backend moved us to a different endpoint on its own. Whether
        // that deserves a pause depends entirely on why: if the device we were
        // on is still in the list, the user changed their default output and
        // wants the music to follow them there; if it is gone, it was taken
        // away and carrying on means playing out loud through whatever the OS
        // fell back to.
        private void HandleReroute()
        {
            var devices = GetOutputDevices();

            lock (_gate)
            {
                if (_disposed || _device == null)
                    return;

                var previous = _activeDeviceId;
                _activeDeviceId = ResolveActiveDeviceId(_outputDeviceId, devices);

                // No idea what we were on, or enumeration failed outright.
                // Staying quiet is the conservative choice: a spurious pause is
                // worse than a missed one, because the user did not ask for it
                // and cannot see why it happened.
                if (previous == null || devices.Count == 0)
                {
                    _logger.LogInformation("Output rerouted, but the previous device is unknown; leaving playback alone");
                    return;
                }

                foreach (var device in devices)
                {
                    if (device.Id != previous)
                        continue;

                    _logger.LogInformation("Output rerouted while the previous device is still present; treating it as a deliberate change");
                    return;
                }

                if (!_started)
                    return;

                _logger.LogInformation("Output rerouted because the previous device disappeared");
            }

            RaiseOutputDeviceLost();
        }

        private List<string> EnumeratedIds()
        {
            var ids = new List<string>();
            foreach (var device in GetOutputDevices())
                ids.Add(device.Id);

            return ids;
        }

        // Onto the UI thread, because the handler on the other end updates
        // ViewModel state - see IAudioSink.OutputDeviceLost. Both callers reach
        // here off a thread pool item, never from the audio callback.
        private void RaiseOutputDeviceLost() =>
            Dispatcher.UIThread.Post(() => OutputDeviceLost?.Invoke(this, EventArgs.Empty));

        private unsafe void RegisterCoreAudioDiagnostics()
        {
            if (!OperatingSystem.IsIOS() || _device == null)
                return;

            if (flower_coreaudio_diagnostics_register(_device) == 0)
                _logger.LogWarning("Could not register native CoreAudio render diagnostics for the output device");
        }

        private unsafe void UnregisterCoreAudioDiagnostics()
        {
            if (!OperatingSystem.IsIOS() || _device == null)
                return;

            flower_coreaudio_diagnostics_unregister(_device);
        }

        private unsafe CoreAudioDiagnosticsSnapshot? TakeCoreAudioDiagnostics()
        {
            if (!OperatingSystem.IsIOS() || _device == null)
                return null;

            CoreAudioDiagnosticsSnapshot snapshot;
            return flower_coreaudio_diagnostics_take_snapshot(_device, out snapshot) != 0
                ? snapshot
                : null;
        }

        [DllImport("miniaudio", EntryPoint = "flower_coreaudio_diagnostics_register")]
        private static extern unsafe int flower_coreaudio_diagnostics_register(ma_device* device);

        [DllImport("miniaudio", EntryPoint = "flower_coreaudio_diagnostics_unregister")]
        private static extern unsafe void flower_coreaudio_diagnostics_unregister(ma_device* device);

        [DllImport("miniaudio", EntryPoint = "flower_coreaudio_diagnostics_take_snapshot")]
        private static extern unsafe int flower_coreaudio_diagnostics_take_snapshot(
            ma_device* device,
            out CoreAudioDiagnosticsSnapshot snapshot);

        // Wrapped whole in a try/catch, unlike anything else in this class:
        // this is an [UnmanagedCallersOnly] boundary, and an exception that
        // reaches it does not unwind into a handler - it takes the process
        // down. Silence is the only safe answer, and it is logged nowhere,
        // because logging from the real-time thread is its own way of causing
        // the glitches this exists to avoid; the watchdog's counters are what
        // surface a callback that has stopped producing.
        [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
        private static void DataCallback(ma_device* pDevice, void* pOutput, void* pInput, uint frameCount)
        {
            var byteCount = checked((int)frameCount * GaplessFormat.BytesPerFrame);
            var dest = new Span<byte>(pOutput, byteCount);

            try
            {
                Render(pDevice, dest, byteCount, frameCount);
            }
            catch
            {
                RecordCallbackException(pDevice);
                dest.Clear();
            }
        }

        // The unmanaged callback boundary cannot let a managed exception
        // escape, so it must still return silence. Counting it is safe on the
        // real-time thread and turns what used to be an invisible dropout into
        // a watchdog error one second later.
        private static void RecordCallbackException(ma_device* pDevice)
        {
            try
            {
                if (pDevice == null)
                    return;

                var handle = GCHandle.FromIntPtr((IntPtr)pDevice->pUserData);
                if (handle.Target is MiniaudioSink sink)
                    Interlocked.Increment(ref sink._callbackExceptionCount);
            }
            catch
            {
                // This is already on the exception path of an unmanaged
                // callback. Diagnostics must never become a second escape.
            }
        }

        private static void Render(ma_device* pDevice, Span<byte> dest, int byteCount, uint frameCount)
        {
            var handle = GCHandle.FromIntPtr((IntPtr)pDevice->pUserData);
            if (handle.Target is not MiniaudioSink sink || sink._ringBuffer is not { } ring)
            {
                dest.Clear();
                return;
            }

            // Capture timestamps rather than logging from the real-time
            // thread. The watchdog reports the sampled maxima later, which
            // makes a mid-song click diagnosable even when the PCM ring never
            // ran short.
            var callbackStartedAt = Stopwatch.GetTimestamp();
            try
            {

                var generation = ring.Generation;

                // Prime latch - see _primed. A flush (a fresh start, a seek, a
                // manual skip) empties the ring while the decoder is still opening
                // the file, and rendering during that window produces a starved
                // trickle rather than audio. Silence instead, until there is
                // enough buffered to play through it.
                if (generation != sink._primeGeneration)
                {
                    sink._primeGeneration = generation;
                    sink._primed = false;
                    sink._primeDeadlineTimestamp = Stopwatch.GetTimestamp()
                        + (long)(Stopwatch.Frequency * (PrimeDeadlineMs / 1000.0));
                }

                if (!sink._primed)
                {
                    var required = sink._outputStage.Timing.PrebufferMs * (long)GaplessFormat.SampleRate
                        * GaplessFormat.BytesPerFrame / 1000;
                    sink._primed = ring.AvailableBytes >= required
                        || Stopwatch.GetTimestamp() >= sink._primeDeadlineTimestamp;
                }

                // Read() never blocks - a short/zero read just means the ring is
                // temporarily empty (decode running behind), not end-of-stream.
                // This callback runs on miniaudio's real-time thread, so the
                // remainder of the requested frames is silence-padded rather
                // than waited for.
                var read = sink._primed ? ring.Read(dest) : 0;
                if (read < byteCount)
                    dest[read..].Clear();

                Interlocked.Increment(ref sink._callbackCount);
                Interlocked.Add(ref sink._requestedBytes, byteCount);
                Interlocked.Add(ref sink._realBytesRendered, read);
                if (read < byteCount)
                {
                    Interlocked.Add(ref sink._silenceBytesRendered, byteCount - read);
                    if (sink._primed)
                        Interlocked.Increment(ref sink._shortReadCount);
                }

                if (read > 0)
                {
                    var fingerprint = Fingerprint(dest[..read], out var audible);
                    var previousFingerprint = Interlocked.Exchange(ref sink._lastCallbackFingerprint, fingerprint);
                    var previousRead = Interlocked.Exchange(ref sink._lastCallbackReadBytes, read);
                    Interlocked.Exchange(ref sink._lastPcmFingerprint, fingerprint);

                    if (audible && previousFingerprint == fingerprint && previousRead == read)
                    {
                        var repeated = Interlocked.Increment(ref sink._consecutiveIdenticalCallbacks);
                        if (repeated > Volatile.Read(ref sink._maxIdenticalCallbackRun))
                            Volatile.Write(ref sink._maxIdenticalCallbackRun, repeated);
                    }
                    else
                    {
                        Interlocked.Exchange(ref sink._consecutiveIdenticalCallbacks, 0);
                    }
                }

                // Skipped entirely while the prime latch is holding: the buffer is
                // already pure silence, and running the output stage over it would
                // spend the declick fade-in on that silence, so the first real
                // audio after a flush would arrive at full gain - the click the
                // fade exists to remove. Not calling Process leaves the stage's
                // last-seen generation untouched, so the first primed callback is
                // the one that arms the fade-in, which is where it belongs.
                if (!sink._primed)
                    return;

                // The whole buffer, silence padding included - the EQ's delay
                // lines and the gain ramp both have to keep advancing across a
                // gap, or they resume from pre-gap state on the other side of it.
                sink._outputStage.Process(dest, generation);
            }
            finally
            {
                sink._callbackTiming.Record(callbackStartedAt, Stopwatch.GetTimestamp(), frameCount, GaplessFormat.SampleRate);
            }
        }

        // Samples one byte per 64 rather than walking every PCM byte on the
        // real-time callback. It is not a content hash; it is a cheap signal
        // for detecting the exact same device buffer being replayed over and
        // over, which is one reported failure shape.
        //
        // Reports through `audible` whether any of the sampled bytes was
        // non-zero, so the caller can tell "the same buffer again" from
        // "silence again". Both hash the same, and only the first of them is
        // worth a warning: a run of silent callbacks is a pause, a fade-out or
        // a starved ring, all of which have counters of their own, while a run
        // of identical *audible* buffers is the repeated-buffer static this
        // exists to catch.
        private static long Fingerprint(ReadOnlySpan<byte> data, out bool audible)
        {
            const ulong offset = 14695981039346656037;
            const ulong prime = 1099511628211;
            var hash = offset;
            byte seen = 0;
            for (var i = 0; i < data.Length; i += 64)
            {
                hash ^= data[i];
                hash *= prime;
                seen |= data[i];
            }

            audible = seen != 0;

            hash ^= (uint)data.Length;
            hash *= prime;
            return unchecked((long)hash);
        }

        public void Resume()
        {
            lock (_gate)
            {
                if (_device == null || _started)
                    return;

                // Armed before the device starts, so the very first callback
                // already renders through the fade-in envelope rather than
                // jumping straight in at whatever amplitude the stream
                // happens to begin on.
                if (_bridge is { } bridge)
                    bridge.BeginFadeIn(FadeFrames(_outputStage.Timing.TransportFadeMs));
                else
                    _outputStage.BeginFadeIn();

                _callbackTiming.Reset();

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

                // ma_device_stop cuts the waveform wherever it happens to be,
                // which is a click. Give the callback TransportFadeMs to walk
                // it down to silence first - bounded, because the callback
                // may never run again and a pause must not hang the caller.
                FadeOutAndWait();
                StopDeviceIntentionally();
                _started = false;
                _callbackTiming.Reset();
                Paused?.Invoke(this, EventArgs.Empty);
            }
        }

        public void Stop()
        {
            lock (_gate)
            {
                if (_device == null || !_started)
                    return;

                // Faded, for the same reason as Pause above.
                FadeOutAndWait();
                StopDeviceIntentionally();
                _started = false;
                _callbackTiming.Reset();
                Stopped?.Invoke(this, EventArgs.Empty);
            }
        }

        // Whichever side of the hand-off owns the transport envelope. In
        // bridge mode it is the native callback, so that a pause is as
        // immediate with a third of a second buffered downstream as it is
        // with nothing; the output stage's own fade only applies when this
        // sink is still rendering in managed code.
        private void FadeOutAndWait()
        {
            if (_bridge is not { } bridge)
            {
                _outputStage.FadeOutAndWait();
                return;
            }

            bridge.BeginFadeOut(FadeFrames(_outputStage.Timing.TransportFadeMs));

            var deadline = Stopwatch.GetTimestamp()
                + (long)(Stopwatch.Frequency
                    * ((_outputStage.Timing.TransportFadeMs + _outputStage.Timing.FadeOutWaitMs) / 1000.0));
            while (!bridge.FadeOutCompleted && Stopwatch.GetTimestamp() < deadline)
                Thread.Sleep(1);
        }

        private static int FadeFrames(int milliseconds) =>
            (int)(milliseconds * (long)GaplessFormat.SampleRate / 1000);

        // See IAudioSink.BufferedBytes - zero without a bridge, where nothing
        // sits between the shared ring and the speaker.
        public long BufferedBytes => _feeder?.BufferedBytes ?? 0;

        public IReadOnlyList<AudioOutputDevice> GetOutputDevices()
        {
            lock (_gate)
            {
                if (_disposed || _context == null)
                    return [];

                // The infos land in memory the context owns and reuses on the
                // next enumeration, so everything wanted out of them is copied
                // into managed objects before the lock is released.
                ma_device_info* infos;
                uint count;
                var result = ma.context_get_devices(_context, &infos, &count, null, null);
                if (result != ma_result.MA_SUCCESS)
                {
                    _logger.LogWarning("miniaudio context_get_devices failed: {Result}", result);
                    return [];
                }

                var devices = new List<AudioOutputDevice>((int)count);
                for (uint i = 0; i < count; i++)
                {
                    var info = &infos[i];
                    devices.Add(new AudioOutputDevice(EncodeDeviceId(&info->id), ReadName(info), info->isDefault != 0));
                }

                return devices;
            }
        }

        public string? OutputDeviceId
        {
            get
            {
                lock (_gate)
                {
                    return _outputDeviceId;
                }
            }
        }

        public void SetOutputDevice(string? deviceId)
        {
            lock (_gate)
            {
                if (_disposed || _context == null || deviceId == _outputDeviceId)
                    return;

                var wasStarted = _started;
                CloseDevice();
                _outputDeviceId = deviceId;

                // A device enumerated a moment ago can be gone by now -
                // unplugging the headphones with the picker open is the
                // obvious way. Silence would be the worst outcome, so fall
                // back to the OS default rather than leaving no device open.
                if (!OpenDevice() && _outputDeviceId != null)
                {
                    _logger.LogWarning("Could not open the requested output device; falling back to the system default");
                    _outputDeviceId = null;
                    OpenDevice();
                }

                if (!wasStarted)
                    return;

                if (_device == null)
                {
                    // Nothing opened at all. The UI still believes playback is
                    // running, so this is the one path that does owe it an
                    // event.
                    Stopped?.Invoke(this, EventArgs.Empty);
                    return;
                }

                var startResult = ma.device_start(_device);
                if (startResult == ma_result.MA_SUCCESS)
                {
                    _started = true;
                }
                else
                {
                    _logger.LogWarning("miniaudio device_start failed after an output change: {Result}", startResult);
                    Stopped?.Invoke(this, EventArgs.Empty);
                }
            }
        }

        // ma_device_id is a fixed-size 256-byte union whose meaningful member
        // depends on the backend (a CoreAudio UID string, a WASAPI wide
        // string, an AAudio int...). Nothing above this class has any business
        // knowing which, so the whole blob is base64'd and handed up as an
        // opaque token that only TryDecodeDeviceId ever reads back.
        private static string EncodeDeviceId(ma_device_id* id) =>
            Convert.ToBase64String(new ReadOnlySpan<byte>(id, sizeof(ma_device_id)));

        private static bool TryDecodeDeviceId(string encoded, ma_device_id* id)
        {
            var destination = new Span<byte>(id, sizeof(ma_device_id));
            return Convert.TryFromBase64String(encoded, destination, out var written)
                   && written == destination.Length;
        }

        // ma_device_info.name is an inline char[256], not a pointer, and
        // taking its address through the binding's generated fixed-buffer
        // wrapper (&info->name) does not give the field's address - so the
        // offset is asked for explicitly and applied to the struct base.
        private static readonly int NameOffset = (int)Marshal.OffsetOf<ma_device_info>(nameof(ma_device_info.name));

        private static string ReadName(ma_device_info* info)
        {
            // MA_MAX_DEVICE_NAME_LENGTH + 1, matching miniaudio.h.
            const int capacity = 256;

            var name = new ReadOnlySpan<byte>((byte*)info + NameOffset, capacity);
            var end = name.IndexOf((byte)0);
            return Encoding.UTF8.GetString(end < 0 ? name : name[..end]);
        }

        public void Dispose()
        {
            lock (_gate)
            {
                if (_disposed)
                    return;
                _disposed = true;

                _watchdog.Dispose();

                CloseDevice();

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
