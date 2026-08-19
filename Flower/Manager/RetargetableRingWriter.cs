using System;
using System.Threading;

namespace Flower.Manager
{
    // The write side of a decoder that can have its output ring swapped
    // underneath it mid-decode: an armed (decode-ahead) TrackDecoder writes
    // into a private staging ring, and at handover GaplessCoordinator calls
    // PromoteTarget to move the staged backlog into the shared ring and
    // point future output there instead.
    //
    // Lives apart from TrackDecoder because that class can only be driven
    // through real LibVLC audio callbacks, and the interesting behaviour
    // here - what happens when a write and a retarget collide on a full
    // ring - is exactly what a real-decode test can't schedule
    // deterministically. See RetargetableRingWriterTests.
    public sealed class RetargetableRingWriter
    {
        private readonly object _gate = new();
        private GaplessRingBuffer _target;

        public RetargetableRingWriter(GaplessRingBuffer target) => _target = target;

        public GaplessRingBuffer Target
        {
            get
            {
                lock (_gate)
                    return _target;
            }
        }

        // Writes all of data to the current target, blocking while it's
        // full (that backpressure is what paces decode to playback).
        //
        // Deliberately not GaplessRingBuffer.Write() under the gate: that
        // blocks *inside* the ring, and a decode-ahead decoder whose
        // staging ring has filled (any current track longer than the
        // staging ring's 60s) blocks there forever - nothing reads a
        // staging ring until handover, and handover's own PromoteTarget
        // can't get in because this write is holding the gate. That
        // deadlock left the promoted track silent with the sink
        // underrunning indefinitely, confirmed from a real session log.
        // Parking on the gate's own monitor instead releases it on each
        // turn, so PromoteTarget can take it, drain the backlog, swap the
        // target and pulse - and this write then finishes into the new
        // target, after the backlog, with nothing lost or reordered.
        //
        // Returns early, having written only part of data, if isAbandoned
        // goes true (the decoder was retired) or the target is Reset()
        // underneath it (a flush/seek - the rest of this chunk belongs to a
        // stream nobody wants anymore), matching what the ring's own
        // blocking Write does on a generation change.
        public void Write(ReadOnlySpan<byte> data, Func<bool>? isAbandoned = null)
        {
            lock (_gate)
            {
                var remaining = data;
                var target = _target;
                var generation = target.Generation;

                while (remaining.Length > 0)
                {
                    if (isAbandoned?.Invoke() == true)
                        return;

                    var written = target.TryWrite(remaining);
                    if (written > 0)
                    {
                        remaining = remaining[written..];
                        continue;
                    }

                    // Times out rather than waiting purely on a pulse: an
                    // ordinary "ring is full, playback will drain it" wait
                    // has nobody to pulse it.
                    Monitor.Wait(_gate, 20);

                    if (!ReferenceEquals(_target, target))
                    {
                        // Promoted while parked: the backlog ahead of these
                        // bytes has already been moved across, so carry on
                        // into the new ring.
                        target = _target;
                        generation = target.Generation;
                    }
                    else if (target.Generation != generation)
                    {
                        // Same ring, Reset() underneath us - a flush or
                        // seek. Checked here rather than after the next
                        // TryWrite, because that reset just freed the room
                        // that would let these now-stale bytes through.
                        return;
                    }
                }
            }
        }

        // Drains everything currently buffered in the old target into
        // newTarget, then switches future writes to it. Atomic with respect
        // to Write above, so no bytes land in a target that's mid-retarget.
        public void PromoteTarget(GaplessRingBuffer newTarget)
        {
            lock (_gate)
            {
                Span<byte> chunk = stackalloc byte[4096];
                int read;
                while ((read = _target.Read(chunk)) > 0)
                    newTarget.Write(chunk[..read]);

                _target = newTarget;

                // Wakes a Write parked because the old target was full.
                Monitor.PulseAll(_gate);
            }
        }

        // Discards whatever is buffered in the current target - a flush or
        // seek, which also makes any parked Write drop the rest of its
        // pre-flush chunk (see Write).
        public void ResetTarget()
        {
            lock (_gate)
            {
                _target.Reset();
                Monitor.PulseAll(_gate);
            }
        }
    }
}
