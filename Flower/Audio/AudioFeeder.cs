using System;
using System.Diagnostics;
using System.Threading;

namespace Flower.Audio
{
    // Moves PCM from the shared GaplessRingBuffer into an IAudioBridge, on an
    // ordinary managed thread.
    //
    // This is the half of the fix that gets managed code off the real-time
    // thread. The prime latch runs here, some hundreds of milliseconds ahead
    // of the speaker, and the native
    // callback is left with the output stage over whatever this has queued. A
    // GC pause that suspends this thread is then just a pause in refilling a
    // buffer already deep enough to play through it, rather than a hole in
    // the output.
    //
    // It copies, and does nothing else to the audio. The output stage - EQ,
    // volume, dither - used to run here, which put a bridge-depth of queue
    // between every control and the speaker: the volume slider and every EQ
    // band lagged by NativeBufferMs (AUDIO-QUALITY-PLAN.md, finding M). The
    // bridge now carries the ring's float as is, and a buffer that holds raw
    // PCM costs no control latency however deep it is.
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

        private readonly GaplessRingBuffer _ring;
        private readonly IAudioBridge _bridge;

        // The device's rate, which the prebuffer and rebuffer are measured in,
        // and the live timing they are read from. One feeder per opened device.
        private readonly uint _sampleRate;
        private readonly Func<AudioTimingSettings> _timing;

        // One chunk on its way from the ring to the bridge.
        private readonly byte[] _chunk;

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

        // See PrimeLatch. Its "buffered" here is the bridge plus the ring behind it: while the
        // latch is closed this thread keeps filling the bridge, so the bridge
        // alone tops out at NativeBufferMs, short of what a rebuffer waits for.
        private readonly PrimeLatch _latch = new();

        public AudioFeeder(GaplessRingBuffer ring, IAudioBridge bridge, uint sampleRate, Func<AudioTimingSettings> timing)
        {
            _ring = ring;
            _bridge = bridge;
            _sampleRate = sampleRate;
            _timing = timing;
            _chunk = new byte[ChunkFrames * GaplessFormat.BytesPerFrame];

            // Adopt the ring's generation rather than flushing on the first
            // tick: a freshly built bridge holds nothing, and a flush nobody
            // is running a callback to acknowledge would stall the first
            // FlushAckTimeoutMs of every session for no reason. The prime
            // latch still starts closed, so nothing renders early.
            _generation = ring.Generation;
            ArmPrimeLatch();
        }

        // Frames handed to the bridge that the device has not rendered yet.
        // Playback position is derived from the shared ring's read cursor,
        // which this thread advances ahead of what is audible, so the caller
        // subtracts this to keep the seek bar honest.
        public int BufferedFrames => _bridge.Available / GaplessFormat.BytesPerFrame;

        public long RebufferCount => _latch.RebufferCount;

        public long TakeLongestRebufferMs() => _latch.TakeLongestRebufferMs();

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

            var moved = Pump();
            _latch.Rendered(moved);
            UpdatePrimeLatch(moved);
            return moved > 0;
        }

        private void BeginFlush()
        {
            _pendingFlush = _bridge.RequestFlush();
            _flushDeadlineTimestamp = Stopwatch.GetTimestamp()
                + (long)(Stopwatch.Frequency * (FlushAckTimeoutMs / 1000.0));
            ArmPrimeLatch();
        }

        private void ArmPrimeLatch() => _latch.ArmAfterFlush(Stopwatch.GetTimestamp());

        private bool TryCompleteFlush()
        {
            if (_pendingFlush == 0 || _bridge.FlushAcked >= _pendingFlush)
                return true;

            if (Stopwatch.GetTimestamp() < _flushDeadlineTimestamp)
                return false;

            _bridge.FlushNow();
            return true;
        }

        private int Pump()
        {
            var moved = 0;

            while (true)
            {
                // Free space is a lower bound - the callback only ever drains -
                // so a read clamped to it is always one the bridge can take
                // whole. That matters: these bytes are already gone from the
                // shared ring by the time the write happens.
                var freeFrames = (_bridge.Capacity - _bridge.Available) / GaplessFormat.BytesPerFrame;
                if (freeFrames <= 0)
                    break;

                var wantFrames = Math.Min(ChunkFrames, freeFrames);
                var read = _ring.Read(_chunk.AsSpan(0, wantFrames * GaplessFormat.BytesPerFrame));
                if (read <= 0)
                    break;

                _bridge.Write(_chunk.AsSpan(0, read));
                moved += read;
            }

            return moved;
        }

        private void UpdatePrimeLatch(int moved)
        {
            var now = Stopwatch.GetTimestamp();

            // The bridge has played out everything it was given and there is
            // nothing behind it: the callback is rendering silence from here
            // on. Three hundred milliseconds of bridge only empties like this
            // when the decoder has fallen behind, so close the latch and let
            // the ring fill rather than playing each scrap as it lands. The
            // bridge is already empty, so un-priming it drops nothing.
            //
            // Un-priming is also what tells the callback that whatever it
            // renders next comes after a gap, which it fades in.
            var timing = _timing();
            if (_latch.IsPrimed && moved == 0 && _bridge.Available == 0 && _ring.AvailableBytes == 0
                && _latch.RanDry(timing, now))
            {
                _bridge.SetPrimed(false);
                return;
            }

            var buffered = (long)BufferedFrames + _ring.AvailableBytes / GaplessFormat.BytesPerFrame;
            var capacity = (long)(_bridge.Capacity + _ring.Capacity) / GaplessFormat.BytesPerFrame;
            if (_latch.TryRelease(buffered, capacity, timing, _sampleRate, now))
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
