using System;
using System.Diagnostics;
using System.Threading;

namespace Flower.Audio
{
    // Moves PCM from the shared GaplessRingBuffer into an IAudioBridge, running
    // the output stage on the way, on an ordinary managed thread.
    //
    // This is the half of the fix that gets managed code off the real-time
    // thread. Everything the render callback used to do here - the prime
    // latch, the EQ, the gain ramp, the dither - happens on this thread
    // instead, some hundreds of milliseconds ahead of the speaker, and the
    // callback is left with a memcpy and a fade. A GC pause that suspends this
    // thread is then just a pause in refilling a buffer that is already deep
    // enough to play through it, rather than a hole in the output.
    //
    // The cost is that a change applied here - the volume slider, an EQ band -
    // reaches the speaker a bridge-depth later. See
    // AudioTimingSettings.NativeBufferMs, which is that trade in one number.
    internal sealed class AudioFeeder : IDisposable
    {
        // 1024 frames, matching the smallest period a device is likely to ask
        // for. Small enough that a flush never has to discard much, big enough
        // that the per-chunk overhead is nothing next to the processing.
        private const int ChunkFrames = 1024;

        // How long to wait for the render callback to acknowledge a flush
        // before applying it here instead. The callback acknowledges on its
        // very next pass, so this only ever expires when there is no callback
        // running at all - a device that stopped between the request and now.
        private const int FlushAckTimeoutMs = 120;

        // Same safety net as MiniaudioSink's own prime latch: a decoder that
        // is never going to deliver must not leave playback silent forever.
        private const int PrimeDeadlineMs = 1500;

        private readonly GaplessRingBuffer _ring;
        private readonly IAudioBridge _bridge;
        private readonly OutputStage _outputStage;
        private readonly byte[] _chunk = new byte[ChunkFrames * GaplessFormat.BytesPerFrame];

        private Thread? _thread;
        private volatile bool _running;

        // Whether the device this feeder fills is actually running. The thread
        // parks on this rather than ticking, because a stopped device drains
        // nothing: every tick would read an empty ring, find nothing, sleep
        // 2ms and do it again - 400-odd wakeups a second, at the highest
        // priority in the process, for as long as the device stayed open.
        //
        // Nothing used to clear it. The feeder was started when the device was
        // opened and disposed only when it was closed, so Pause() and Stop()
        // left it spinning - and since GaplessRingBuffer.Read counts *any*
        // read of an empty ring as an underrun, every one of those ticks
        // scored one. A phone sitting idle overnight logged "Render watchdog:
        // underrun(s) detected - Started=False ... (+435)" every second for as
        // long as it was left alone, which is both the drain itself and what
        // kept the log archive rewriting (see DeviceLogArchive.Ingest).
        private readonly ManualResetEventSlim _awake = new(false);

        private int _generation = int.MinValue;
        private long _pendingFlush;
        private long _flushDeadlineTimestamp;
        private long _primeDeadlineTimestamp;
        private bool _primed;

        public AudioFeeder(GaplessRingBuffer ring, IAudioBridge bridge, OutputStage outputStage)
        {
            _ring = ring;
            _bridge = bridge;
            _outputStage = outputStage;

            // Adopt the ring's generation rather than flushing on the first
            // tick: a freshly built bridge holds nothing, and a flush nobody
            // is running a callback to acknowledge would stall the first
            // FlushAckTimeoutMs of every session for no reason. The prime
            // latch still starts closed, so nothing renders early.
            _generation = ring.Generation;
            ArmPrimeLatch();
        }

        // Bytes handed to the bridge that the device has not rendered yet.
        // Playback position is derived from the shared ring's read cursor,
        // which this thread advances ahead of what is audible, so the caller
        // subtracts this to keep the seek bar honest.
        public int BufferedBytes => _bridge.Available;

        // Starts the thread parked. The device is open but stopped at this
        // point (see MiniaudioSink.OpenDevice), and there is nothing to fill
        // ahead of a device that is not running.
        public void Start()
        {
            if (_thread != null)
                return;

            _running = true;
            _thread = new Thread(Run)
            {
                IsBackground = true,
                Name = "Flower audio feeder",
                // Above everything else in the process but still an ordinary
                // managed thread: it is allowed to be late, it just must not
                // queue behind UI work or a library scan while it is.
                Priority = ThreadPriority.Highest,
            };
            _thread.Start();
        }

        // Follows the device: called once it is actually running, so the
        // bridge starts filling behind a callback that is there to drain it.
        //
        // Re-arms the prime latch, because a deadline that has been sitting
        // parked for the length of a pause has long since expired, and
        // arriving already primed is the one thing the latch exists to
        // prevent.
        public void Resume()
        {
            ArmPrimeLatch();
            _awake.Set();
        }

        // Parked, not stopped: the thread stays alive and the bridge keeps
        // whatever it holds, so a resume is a wake rather than a restart.
        public void Pause() => _awake.Reset();

        private void Run()
        {
            while (_running)
            {
                if (!_awake.IsSet)
                {
                    // No timeout: Dispose sets this on its way past, so a
                    // parked thread still leaves promptly.
                    _awake.Wait();
                    continue;
                }

                // A tick that moved nothing means the ring is empty or the
                // bridge is full; either way the next opportunity is a device
                // period away, so there is nothing to gain from spinning.
                if (!Tick())
                    Thread.Sleep(2);
            }
        }

        // One pass, exposed for tests: everything in here is ordinary logic
        // that a fake bridge can drive deterministically.
        public bool Tick()
        {
            var generation = _ring.Generation;
            if (generation != _generation)
            {
                _generation = generation;
                BeginFlush();
            }

            // Nothing may be read from the shared ring while a flush is
            // outstanding. The bridge refuses writes until the callback has
            // dropped what it holds, and bytes read here are consumed - a
            // write that came back short would lose them outright.
            if (!TryCompleteFlush())
                return false;

            var moved = Pump(generation);
            UpdatePrimeLatch();
            return moved > 0;
        }

        private void BeginFlush()
        {
            _pendingFlush = _bridge.RequestFlush();
            _flushDeadlineTimestamp = Stopwatch.GetTimestamp()
                + (long)(Stopwatch.Frequency * (FlushAckTimeoutMs / 1000.0));
            ArmPrimeLatch();
        }

        private void ArmPrimeLatch()
        {
            _primed = false;
            _primeDeadlineTimestamp = Stopwatch.GetTimestamp()
                + (long)(Stopwatch.Frequency * (PrimeDeadlineMs / 1000.0));
        }

        private bool TryCompleteFlush()
        {
            if (_pendingFlush == 0 || _bridge.FlushAcked >= _pendingFlush)
                return true;

            if (Stopwatch.GetTimestamp() < _flushDeadlineTimestamp)
                return false;

            _bridge.FlushNow();
            return true;
        }

        private int Pump(int generation)
        {
            var moved = 0;

            while (true)
            {
                // Free space is a lower bound - the callback only ever drains -
                // so a read clamped to it is always one the bridge can take
                // whole. That matters: these bytes are already gone from the
                // shared ring by the time the write happens.
                var free = _bridge.Capacity - _bridge.Available;
                if (free < GaplessFormat.BytesPerFrame)
                    break;

                var want = Math.Min(_chunk.Length, free);
                var read = _ring.Read(_chunk.AsSpan(0, want));
                if (read <= 0)
                    break;

                _outputStage.Process(_chunk.AsSpan(0, read), generation);
                _bridge.Write(_chunk.AsSpan(0, read));
                moved += read;
            }

            return moved;
        }

        private void UpdatePrimeLatch()
        {
            if (_primed)
                return;

            var required = _outputStage.Timing.PrebufferMs * (long)GaplessFormat.SampleRate
                * GaplessFormat.BytesPerFrame / 1000;
            if (_bridge.Available < required && Stopwatch.GetTimestamp() < _primeDeadlineTimestamp)
                return;

            _primed = true;
            _bridge.SetPrimed(true);
        }

        public void Dispose()
        {
            if (_thread == null)
                return;

            _running = false;
            // Unparks a thread waiting on the gate above, which then sees
            // _running and returns rather than ticking.
            _awake.Set();

            var exited = _thread.Join(TimeSpan.FromSeconds(1));
            _thread = null;

            // Only once the thread has actually gone. Disposing the gate out
            // from under one still parked on it throws on that thread, where
            // there is nobody to catch it - and the join above is bounded, so
            // "it did not exit" is a state this has to survive.
            if (exited)
                _awake.Dispose();
        }
    }
}
