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

        private void Run()
        {
            while (_running)
            {
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
            _running = false;
            _thread?.Join(TimeSpan.FromSeconds(1));
            _thread = null;
        }
    }
}
