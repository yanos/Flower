namespace Flower.Audio
{
    // What happened at one gapless handover, measured across the window that
    // actually decides whether the handover was audible: from entering
    // RetargetableRingWriter.PromoteTarget to the instant the promoted
    // track's first bytes reach the shared ring the render callback reads.
    //
    // Deliberately not the whole of PromoteTarget. That call drains the armed
    // decoder's staged backlog - up to GaplessCoordinator's
    // DefaultStagingCapacityBytes, a full minute of audio - and is paced the
    // whole way by real-time playback backpressure, so measuring across all of
    // it would report the new track's first minute of playback as if it were
    // the seam.
    //
    // A splice that moved nothing (BytesMoved == 0) leaves the two
    // first-byte fields at -1: there was no first byte to time, which is
    // itself worth a warning - an armed decoder that staged no audio at all
    // means the handover has nothing to hand over.
    public readonly record struct PromotionSplice(
        long StagedBytes,
        long BytesMoved,
        double MillisecondsToFirstByte,
        long DestinationUnderrunsAtFirstByte,
        double TotalMilliseconds)
    {
        public bool MovedAnything => BytesMoved > 0;
    }
}
