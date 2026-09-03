namespace Flower.Audio
{
    // Why a decode-ahead prepare did or did not succeed.
    //
    // A bool here was actively misleading. Every remote track's prepare
    // returned false, the coordinator logged "Decode-ahead prepare failed"
    // with no cause attached, and there was no way to tell from the log
    // whether the server had gone away, the file was unplayable, or - as it
    // turned out - the parse had never been attempted at all. Those want
    // different responses and read as the same line.
    public enum DecodePrepareResult
    {
        // Parsed; the decoder can be armed.
        Ready,

        // The parse was not attempted. Not a failure of the track or the
        // server: LibVLC skips media that the requested parse options do not
        // cover, which is how a network stream comes back from a local-only
        // parse.
        NotAttempted,

        // The parse was attempted and did not answer in time. This is the
        // "server is not responding" case for a streamed track, and the only
        // one of these that says anything about the network.
        TimedOut,

        // The parse was attempted and the media was rejected - an unplayable
        // or unreachable track, as opposed to a slow one.
        Failed,

        // The decoder was retired underneath the prepare. Expected during a
        // skip or a queue change, and not worth reporting.
        Retired,
    }
}
