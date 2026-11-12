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
        private readonly ILogger<MiniaudioSink> _logger;
        private readonly object _gate = new();
        private GaplessRingBuffer? _ringBuffer;

        // Non-null while a device is open. The render callback is pure C in
        // libflowerbridge, draining _bridge and running the output stage, and
        // _feeder is the managed thread that fills it. There is no other
        // render path: a device is never opened without one (OpenDevice). See
        // NativeAudioBridge.
        //
        // _bridge is assigned and freed under _stageGate, and read under it by
        // the controls below: they deliberately stay off _gate, which a device
        // open holds for as long as the platform takes, and a control reaching
        // a bridge being freed would be a use-after-free in native code.
        private NativeAudioBridge? _bridge;
        private AudioFeeder? _feeder;
        private readonly object _stageGate = new();

        // The output stage's controls as last set: the volume's target gain,
        // the EQ curve and the timing. The stage itself - EQ, gain ramp,
        // declick, dithered requantisation to the device's format - runs in
        // the bridge's callback, and none of it can be left to miniaudio, whose
        // own volume is a truncating integer multiply. These are what each new
        // bridge is loaded with (PushStage). Written under _stageGate.
        private volatile AudioTimingSettings _timing = new AudioTimingSettings().Clamped();
        private float _targetGain = 1f;
        private Equalizer? _equalizer;

        // What this sink consumes - see IAudioSink.Format. The first
        // OpenDevice settles its rate.
        private PcmFormat _format = PcmFormat.Default;

        // The device's own sample format, which the output stage requantises
        // to. Settled again by every open.
        private DeviceSampleFormat _deviceFormat = DeviceSampleFormat.S24;

        public PcmFormat Format => _format;

        private ma_context* _context;
        private ma_device* _device;
        private volatile bool _started;
        private bool _disposed;

        // The first opened device establishes the canonical PCM rate for the
        // lifetime of this sink. An output-device change may need miniaudio to
        // resample, but changing the rate while current and armed decoders
        // exist would corrupt timing and playback speed.
        private bool _hasNegotiatedRate;

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
        // ma_device: it is applied by the output stage, not by miniaudio, whose
        // own master volume is pinned at 1.0 (its volume path is a truncating,
        // undithered integer multiply).
        private volatile int _volumePercent = 100;

        // The notification callback is [UnmanagedCallersOnly] - it can't close
        // over instance state, so this instance's GCHandle is stashed in
        // ma_device_config.pUserData (copied onto ma_device.pUserData by
        // miniaudio) as the only way back from it to this sink. The render
        // callback finds its bridge by device pointer instead. Freed in
        // Dispose().
        private GCHandle _selfHandle;

        // Diagnostic-only: watches ring.UnderrunCount once a second so a
        // crackling/glitching report can be correlated against actual
        // buffer underruns (ring momentarily empty when the callback asked
        // for data) versus some other cause (format/rate mismatch, etc).
        // Only logs when the underrun count actually moves or the device's
        // running state changes - a healthy render loop ticks silently.
        //
        // "Moves" is an edge, not a level - see UnderrunRunTracker, which is
        // where that distinction and the reason for it live.
        private readonly Timer _watchdog;
        private readonly UnderrunRunTracker _underruns = new();
        private long _watchdogLastShortReadCount;
        private bool _watchdogLastStarted;
        private long _watchdogLastRealBytesRendered;
        private int _watchdogNoProgressTicks;
        private int _watchdogTickCount;

        // Running totals of the bridge's windowed counters, folded in once per
        // watchdog tick (FoldBridgeCounters). The callback itself only ever
        // touches atomics in C; formatting or logging from the audio thread
        // could cause the very underruns these exist to diagnose.
        private long _callbackCount;
        private long _requestedBytes;
        private long _realBytesRendered;
        private long _silenceBytesRendered;
        private long _shortReadCount;
        private long _lastPcmFingerprint;
        private int _lastCallbackReadBytes;
        private int _maxIdenticalCallbackRun;

        // The bridge's counters tell us whether Flower supplied PCM on time.
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

        // MaxSampleDelta, AbruptFrames, RepeatedBuffers and MaxRepeatedBufferRun
        // are the only counters here derived from the samples themselves, and
        // they are computed inside the vendored CoreAudio callback - see
        // ma_on_output__coreaudio in native/miniaudio/vendor/miniaudio.h, which
        // inspects a buffer only when internalFormat is s16 or f32.
        //
        // Flower opens the device at s16 or s24 and never f32, so on a device
        // that took s24 - most of them - nothing walks the PCM at all and all
        // four counters stay at zero forever. Zero reads as "looked and found
        // no discontinuities", which is the exact opposite of what it means.
        // Printed as n/a instead, until the native side grows an s24 path
        // (which needs the vendored binaries rebuilt - see
        // native/miniaudio/README.md).
        private bool PcmShapeCountersApply => _deviceFormat == DeviceSampleFormat.S16;

        private string PcmShapeCounter(ulong value) =>
            PcmShapeCountersApply ? value.ToString() : "n/a";

        private string PcmShapeCounter(uint value) =>
            PcmShapeCountersApply ? value.ToString() : "n/a";

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
        // never tried this path. The default probing doesn't know the real
        // on-disk name.
        //
        // Registered for this assembly too, for its own imports: the
        // CoreAudio diagnostics below, which are miniaudio's, and
        // NativeAudioBridge's, which are flowerbridge.framework's - the same
        // nested layout, so the same lookup. The diagnostics used to resolve
        // only because Mono reuses a library another assembly already loaded
        // under that name, which holds in the app, which opens a context
        // first, and not for anything that probes the bridge before touching
        // miniaudio: the native output stage's checks found it reporting no
        // bridge in the Mono iOS suite. Under NativeAOT neither resolver is consulted
        // (DirectPInvoke, in Flower.iOS.csproj).
        //
        // Nowhere else needs one. Android finds lib<name>.so in the APK by
        // name, and desktop finds the NuGet's miniaudio through the deps file
        // and libflowerbridge flat beside the app (FlowerBridge.props).
        static MiniaudioSink()
        {
            if (OperatingSystem.IsIOS())
            {
                NativeLibrary.SetDllImportResolver(typeof(ma).Assembly, ResolveIosFramework);
                NativeLibrary.SetDllImportResolver(typeof(MiniaudioSink).Assembly, ResolveIosFramework);
            }
        }

        private static IntPtr ResolveIosFramework(string libraryName, System.Reflection.Assembly assembly, DllImportSearchPath? searchPath)
        {
            if (libraryName is not ("miniaudio" or NativeAudioBridge.LibraryName))
                return IntPtr.Zero;

            var path = Path.Combine(AppContext.BaseDirectory, "Frameworks", $"{libraryName}.framework", libraryName);
            return NativeLibrary.TryLoad(path, out var handle) ? handle : IntPtr.Zero;
        }

        public MiniaudioSink(ILogger<MiniaudioSink> logger)
        {
            _logger = logger;

            // Armed when playback starts, not here: a timer started with the
            // sink woke the process once a second for its whole life, idle in
            // a phone's background included. One shot at a time and re-armed
            // by the tick itself, so a slow tick cannot overlap the next -
            // FoldBridgeCounters must run once per tick and nowhere else.
            _watchdog = new Timer(1000) { AutoReset = false };
            _watchdog.Elapsed += (_, _) => WatchdogTick();
        }

        // Called wherever playback starts. Starting a timer that is already
        // running leaves it as it is.
        private void ArmWatchdog()
        {
            if (!_disposed)
                _watchdog.Start();
        }

        // Re-armed while playing, and so once more after playback stops: the
        // tick that sees the device stopped is the one that logs it, and the
        // last until something plays.
        private void WatchdogTick()
        {
            if (Interlocked.Exchange(ref _watchdogTicking, 1) == 1)
                return;

            try
            {
                LogWatchdogTick();
            }
            finally
            {
                Volatile.Write(ref _watchdogTicking, 0);
            }

            if (_started)
            {
                try
                {
                    ArmWatchdog();
                }
                catch (ObjectDisposedException)
                {
                    // Disposed between the check and the start.
                }
            }
        }

        private int _watchdogTicking;
        private long _watchdogLastRebufferCount;

        private void LogWatchdogTick()
        {
            var ring = _ringBuffer;
            if (ring == null)
                return;

            // The counters live in C, on the far side of the hand-off, and
            // are folded into running totals here once a tick.
            var callbackTiming = FoldBridgeCounters();

            var underrunCount = ring.UnderrunCount;
            var started = _started;
            var realBytesRendered = Interlocked.Read(ref _realBytesRendered);
            var shortReadCount = Interlocked.Read(ref _shortReadCount);
            var newShortReads = shortReadCount - Interlocked.Read(ref _watchdogLastShortReadCount);
            Interlocked.Exchange(ref _watchdogLastShortReadCount, shortReadCount);
            var maxIdenticalRun = Interlocked.Exchange(ref _maxIdenticalCallbackRun, 0);
            var coreAudioDiagnostics = TakeCoreAudioDiagnostics();

            if (started && realBytesRendered == _watchdogLastRealBytesRendered)
                _watchdogNoProgressTicks++;
            else
                _watchdogNoProgressTicks = 0;

            var underruns = _underruns.Observe(underrunCount);

            // Its own line, outside the chain below: a rebuffer comes with
            // underruns by definition, and the reader needs both to tell "the
            // stream fell behind and playback paused for it" from a glitch.
            var feeder = _feeder;
            var rebufferCount = feeder?.RebufferCount ?? 0;
            var longestRebufferMs = feeder?.TakeLongestRebufferMs() ?? 0;

            // A device reopen builds a new feeder, whose count starts again.
            if (rebufferCount < _watchdogLastRebufferCount)
                _watchdogLastRebufferCount = 0;

            var newRebuffers = rebufferCount - _watchdogLastRebufferCount;
            _watchdogLastRebufferCount = rebufferCount;
            if (newRebuffers > 0 || longestRebufferMs > 0)
            {
                _logger.LogWarning(
                    "Render watchdog: output ran dry mid-track and is rebuffering - Rebuffers={Rebuffers} (+{NewRebuffers}) LongestResumedAfterMs={LongestMs} RebufferMs={RebufferMs} RingAvailable={Available}/{Capacity} BridgeBufferedFrames={BridgeBuffered}",
                    rebufferCount, newRebuffers, longestRebufferMs,
                    _timing.RebufferMs, ring.AvailableBytes, ring.Capacity, feeder?.BufferedFrames ?? 0);
            }

            if (underruns.Cleared is { } run)
            {
                _logger.LogInformation(
                    "Render watchdog: underruns stopped after {RunSeconds}s - Underruns={Underruns} (+{RunUnderruns} over the run) Started={Started} RingAvailable={Available}/{Capacity}",
                    run.Ticks, underrunCount, run.Underruns, started, ring.AvailableBytes, ring.Capacity);
            }

            if (underruns.Log)
            {
                _logger.LogWarning(
                    "Render watchdog: underrun(s) detected - Started={Started} RingAvailable={Available}/{Capacity} Underruns={Underruns} (+{NewUnderruns}) UnderrunningFor={RunSeconds}s",
                    started, ring.AvailableBytes, ring.Capacity, underrunCount, underruns.New, underruns.RunTicks);
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
                    coreAudioPressure.ActionFlags, PcmShapeCounter(coreAudioPressure.MaxSampleDelta), PcmShapeCounter(coreAudioPressure.AbruptFrameCount),
                    PcmShapeCounter(coreAudioPressure.RepeatedBufferCount), PcmShapeCounter(coreAudioPressure.MaxRepeatedBufferRun));
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
                        coreAudioSnapshot.ActionFlags, PcmShapeCounter(coreAudioSnapshot.MaxSampleDelta), PcmShapeCounter(coreAudioSnapshot.AbruptFrameCount),
                        PcmShapeCounter(coreAudioSnapshot.RepeatedBufferCount), PcmShapeCounter(coreAudioSnapshot.MaxRepeatedBufferRun));
                }
            }

            _watchdogLastStarted = started;
            _watchdogLastRealBytesRendered = realBytesRendered;
        }

        // Reading the native snapshot resets it, so this must happen exactly
        // once per watchdog tick and nowhere else. Returns the window's
        // callback timing, which is not a running total.
        //
        // Under _stageGate because this runs on the watchdog's timer, which
        // does not hold _gate, and CloseDevice frees the bridge.
        private AudioCallbackTimingSnapshot FoldBridgeCounters()
        {
            AudioBridgeSnapshot snapshot;
            lock (_stageGate)
            {
                if (_bridge is not { } bridge)
                    return default;

                snapshot = bridge.TakeSnapshot();
            }

            Interlocked.Add(ref _callbackCount, snapshot.CallbackCount);
            Interlocked.Add(ref _requestedBytes, snapshot.RequestedBytes);
            Interlocked.Add(ref _realBytesRendered, snapshot.RealBytes);
            Interlocked.Add(ref _silenceBytesRendered, snapshot.SilenceBytes);
            Interlocked.Add(ref _shortReadCount, snapshot.ShortReadCount);
            Interlocked.Exchange(ref _lastPcmFingerprint, snapshot.LastPcmFingerprint);

            Volatile.Write(ref _lastCallbackReadBytes, (int)snapshot.LastReadBytes);
            if (snapshot.MaxIdenticalCallbackRun > Volatile.Read(ref _maxIdenticalCallbackRun))
                Volatile.Write(ref _maxIdenticalCallbackRun, snapshot.MaxIdenticalCallbackRun);

            return snapshot.Timing;
        }

        public bool IsPlaying => _started;

        public int Volume
        {
            get => _volumePercent;

            // Not _gate and no device call: this is a single store the render
            // callback picks up on its next pass and ramps toward over
            // GainRampMs. Taking _gate here used to put the
            // UI thread behind whatever else held it, a device open included,
            // for a value nothing but the callback reads. _stageGate is held
            // only around the store itself.
            set
            {
                var percent = Math.Clamp(value, 0, 100);
                _volumePercent = percent;
                lock (_stageGate)
                {
                    _targetGain = VolumeCurve.GainForPercent(percent);
                    _bridge?.SetTargetGain(_targetGain);
                }
            }
        }

        public void ApplyEqualizer(Equalizer? equalizer)
        {
            lock (_stageGate)
            {
                // The same curve again is no change. The native side cannot
                // tell, so it would crossfade into a copy with its delay lines
                // zeroed.
                if (ReferenceEquals(_equalizer, equalizer))
                    return;

                _equalizer = equalizer;
                _bridge?.SetEqualizer(equalizer);
            }
        }

        public void ApplyTiming(AudioTimingSettings timing)
        {
            lock (_stageGate)
            {
                _timing = timing.Clamped();
                if (_bridge is { } bridge)
                    PushTiming(bridge);
            }
        }

        // Everything the native output stage reads, for a bridge just made or
        // for new timing. The caller holds _stageGate.
        private void PushTiming(NativeAudioBridge bridge)
        {
            var timing = _timing;
            bridge.SetTiming(FadeFrames(timing.GainRampMs), FadeFrames(timing.DeclickFadeMs));
        }

        private void PushStage(NativeAudioBridge bridge)
        {
            bridge.SetTargetGain(_targetGain);
            bridge.SetEqualizer(_equalizer);
            PushTiming(bridge);
        }

        public void Start(GaplessRingBuffer ringBuffer)
        {
            lock (_gate)
            {
                _ringBuffer = ringBuffer;

                // Once per sink, however many times Start runs - including
                // again after a first call that bailed below without a device.
                // Overwriting an allocated handle would leak it, and with it
                // this sink, its ring buffer and its device, rooted for the
                // life of the process. Dispose frees the one there is.
                if (!_selfHandle.IsAllocated)
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
            // The bridge is the only render path, so a build without it - a
            // RID nobody built, a phone packaged without the framework - is
            // refused here rather than opened onto silence. FlowerBridge.props
            // already fails a desktop build that is missing it.
            if (!NativeAudioBridge.IsAvailable)
            {
                _logger.LogCritical(
                    "No usable {Library} library is loaded, and it is the only way Flower renders audio; not opening a device",
                    NativeAudioBridge.LibraryName);
                return false;
            }

            if (_ringBuffer is not { } ringBuffer)
                return false;

            var config = ma.device_config_init(ma_device_type.ma_device_type_playback);
            var deviceFormat = PreferredDeviceFormat();
            config.playback.format = MiniaudioFormatFor(deviceFormat);
            config.playback.channels = GaplessFormat.Channels;
            // During the first open, zero asks miniaudio for the endpoint's
            // native rate. The decoder is configured from that result before
            // playback begins, leaving FFmpeg as the only sample-rate
            // converter in the chain. Keep that rate on later device changes:
            // existing current and armed decoders cannot safely change rate.
            config.sampleRate = _hasNegotiatedRate ? _format.SampleRate : 0;
            // The bridge's callback is a native symbol in libflowerbridge,
            // which miniaudio calls like any other C function pointer, so
            // installing it costs nothing and needs no managed thunk. It
            // renders silence until AttachTo binds a bridge to this device
            // below, which happens before anything can start the device.
            //
            // No format gate: the bridge carries the pipeline's float and
            // requantises to whichever width the device takes, S16 or S24.
            config.dataCallback = NativeAudioBridge.RenderCallback;
            config.notificationCallback = &NotificationCallback;
            config.pUserData = (void*)GCHandle.ToIntPtr(_selfHandle);

            // Conservative rather than miniaudio's default low-latency profile:
            // a bigger period is a bigger per-period safety margin, chosen when
            // the render callback was managed code, and kept until low latency
            // (AUDIO-QUALITY-PLAN.md, §2) has measured what a shorter one
            // would cost the bridge's headroom.
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
            // asked for 24 bits, and a refusal here is what says the output
            // stage has to requantise to S16 instead - which is a real answer
            // rather than a failure, because every device takes it.
            //
            // At every open, not just the first. The pipeline is float up to
            // the output stage, so no decoder knows or cares what the device
            // takes, and a later device can be given its own answer - unlike
            // the rate.
            if (result != ma_result.MA_SUCCESS && deviceFormat != DeviceSampleFormat.S16)
            {
                _logger.LogWarning(
                    "miniaudio refused a {Format} playback device ({Result}); falling back to 16-bit",
                    deviceFormat, result);
                deviceFormat = DeviceSampleFormat.S16;
                config.playback.format = MiniaudioFormatFor(deviceFormat);

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

            if (!_hasNegotiatedRate)
            {
                var nativeSampleRate = device->sampleRate;
                if (nativeSampleRate == 0)
                    _logger.LogWarning("miniaudio did not report a device sample rate; retaining {SampleRate}Hz", _format.SampleRate);
                else
                    _format = _format with { SampleRate = nativeSampleRate };

                // Said out loud because the failure it guards against is
                // otherwise completely silent. This value is frozen here and
                // never revisited, so a device that was not ready to be asked
                // costs every track for the rest of the process - and the
                // symptom is not a crash or a glitch but merely dull audio,
                // which no automated check in this repo can fail: the fixtures
                // are 440Hz sine waves and a 440Hz sine survives any amount of
                // low-passing intact. An iOS session that had never been
                // activated reported 8000Hz and the whole library played back
                // through 4kHz of bandwidth.
                //
                // A warning rather than a refusal: no ordinary playback device
                // runs below 32kHz, but "ordinary" is not something this layer
                // can rule on, and a phone routed somewhere strange is still
                // owed its audio. The platform's own fix is
                // IPlatformAudioSession.PrepareForOutput, which runs before
                // this; this is the tripwire for the next platform that needs
                // one and does not have one yet.
                if (nativeSampleRate is > 0 and < 32000)
                {
                    _logger.LogWarning(
                        "The output device reports {SampleRate}Hz, which is below any ordinary playback rate. "
                        + "The pipeline is now frozen there and every track will be resampled down to it. "
                        + "This usually means the device was opened before the platform's audio session was ready",
                        nativeSampleRate);
                }

                _hasNegotiatedRate = true;
            }

            _deviceFormat = deviceFormat;
            _logger.LogInformation(
                "Rendering the output device's native {SampleRate}Hz at {Format}",
                _format.SampleRate, deviceFormat);

            // Pinned at unity, deliberately: miniaudio applies its master
            // volume after the data callback as
            // `(ma_int16)(sample * factor)` - a truncating, unrounded,
            // undithered integer multiply. The output stage does the gain in
            // float before requantising, so this path must never be used.
            ma.device_set_master_volume(_device, 1f);

            // After the sample-rate negotiation above, so the buffer is sized
            // in the rate actually being rendered rather than the assumed one,
            // and in the pipeline's frames: the bridge holds what the ring
            // holds, and the output stage runs on the far side of it.
            var capacityFrames = _timing.NativeBufferMs * (long)_format.SampleRate / 1000;
            var bridge = NativeAudioBridge.TryCreate((int)capacityFrames, deviceFormat, _format.SampleRate);
            if (bridge == null)
            {
                // A failed malloc of a third of a second of PCM. Never seen,
                // and without a bridge the device would only render silence.
                _logger.LogError("Could not allocate the native audio bridge; not opening a device");
                UnregisterCoreAudioDiagnostics();
                ma.device_uninit(_device);
                NativeMemory.Free(_device);
                _device = null;
                return false;
            }

            // Published and loaded with the controls' current values in one
            // step, so a control set meanwhile either lands in the fields
            // before the push or reaches this bridge after.
            lock (_stageGate)
            {
                _bridge = bridge;
                PushStage(bridge);
            }

            if (!bridge.AttachTo(_device))
                _logger.LogError("The native audio bridge could not register the output device; playback will be silent");

            _feeder = new AudioFeeder(ringBuffer, bridge, _format.SampleRate, () => _timing);
            _feeder.Start();
            _logger.LogInformation(
                "Rendering through the native audio bridge: {BufferMs}ms ({Capacity} bytes) ahead of the device, "
                + "output stage in the callback",
                _timing.NativeBufferMs, bridge.Capacity);

            _activeDeviceId = ResolveActiveDeviceId(_outputDeviceId, GetOutputDevices());
            return true;
        }

        // S24, unless FLOWER_DEVICE_FORMAT=s16 asks for the narrower one.
        //
        // A listening aid, not a setting. The S16 fallback below is the path a
        // device refusing 24 bits would take, and in shared mode nothing
        // refuses: miniaudio converts whatever it is handed into whatever the
        // endpoint takes. So the dithered requantisation to 16 bits is
        // reachable on no real hardware, and this is how to hear it.
        // Anything else in the variable is ignored, out loud.
        private DeviceSampleFormat PreferredDeviceFormat()
        {
            var requested = Environment.GetEnvironmentVariable(DeviceFormatVariable);
            if (string.IsNullOrEmpty(requested))
                return DeviceSampleFormat.S24;

            if (string.Equals(requested, "s16", StringComparison.OrdinalIgnoreCase))
            {
                _logger.LogInformation("{Variable}={Value}: opening the device at 16-bit", DeviceFormatVariable, requested);
                return DeviceSampleFormat.S16;
            }

            if (!string.Equals(requested, "s24", StringComparison.OrdinalIgnoreCase))
                _logger.LogWarning("Ignoring {Variable}={Value}; it takes s16 or s24", DeviceFormatVariable, requested);

            return DeviceSampleFormat.S24;
        }

        private const string DeviceFormatVariable = "FLOWER_DEVICE_FORMAT";

        // The device format in miniaudio's own terms. ma_format_s24 is packed
        // three-byte little-endian, the same layout DeviceSampleFormat.S24
        // names and the output stage writes, so miniaudio converts nothing between
        // the callback's buffer and the device.
        internal static ma_format MiniaudioFormatFor(DeviceSampleFormat format) =>
            format == DeviceSampleFormat.S24 ? ma_format.ma_format_s24 : ma_format.ma_format_s16;

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

            lock (_stageGate)
            {
                _bridge?.DetachFromDevice();
                _bridge?.Dispose();
                _bridge = null;
            }

            NativeMemory.Free(_device);
            _device = null;
            _started = false;
            _activeDeviceId = null;
        }

        // device_stop makes miniaudio raise ma_device_notification_type_stopped,
        // synchronously and on this thread. Everything Flower stops on purpose
        // goes through here so NotificationCallback can tell that apart from
        // the output disappearing underneath us, which arrives as the same
        // notification. The caller holds _gate.
        private void StopDeviceIntentionally()
        {
            // The feeder exists to keep a running device fed, so it parks
            // whenever that device stops. Before the stop rather than after:
            // a tick that lands in between would read the ring for bytes the
            // device is no longer going to render, and those bytes are gone
            // from the ring once read.
            _feeder?.Pause();

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
                ArmWatchdog();

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
                _bridge?.BeginFadeIn(FadeFrames(_timing.TransportFadeMs));
                _bridge?.ResetTiming();

                var result = ma.device_start(_device);
                if (result != ma_result.MA_SUCCESS)
                {
                    _logger.LogWarning("miniaudio device_start failed: {Result}", result);
                    return;
                }

                // After the device is running, not before: the feeder's first
                // tick may have a flush to carry through, and a flush is
                // acknowledged by the render callback - which only runs on a
                // started device. Asking for one first would spend the whole
                // FlushAckTimeoutMs waiting for an acknowledgement nobody was
                // there to give.
                _feeder?.Resume();

                _started = true;
                ArmWatchdog();
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
                _bridge?.ResetTiming();
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
                _bridge?.ResetTiming();
                Stopped?.Invoke(this, EventArgs.Empty);
            }
        }

        // The native callback owns the transport envelope, so that a pause is
        // as immediate with a third of a second buffered downstream as it is
        // with nothing. Bounded, because the callback may never run again and
        // a transport command must not hang the UI thread on that.
        private void FadeOutAndWait()
        {
            if (_bridge is not { } bridge)
                return;

            var timing = _timing;
            bridge.BeginFadeOut(FadeFrames(timing.TransportFadeMs));

            var deadline = Stopwatch.GetTimestamp()
                + (long)(Stopwatch.Frequency
                    * ((timing.TransportFadeMs + timing.FadeOutWaitMs) / 1000.0));
            while (!bridge.FadeOutCompleted && Stopwatch.GetTimestamp() < deadline)
                Thread.Sleep(1);
        }

        private int FadeFrames(int milliseconds) =>
            (int)(milliseconds * (long)_format.SampleRate / 1000);

        // See IAudioSink.BufferedFrames - zero while no device is open.
        public long BufferedFrames => _feeder?.BufferedFrames ?? 0;

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
                    // Same order as Resume(): the feeder follows the device.
                    // This one is a fresh feeder - CloseDevice disposed the
                    // old one and OpenDevice built another, parked.
                    _feeder?.Resume();
                    _started = true;
                    ArmWatchdog();
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
