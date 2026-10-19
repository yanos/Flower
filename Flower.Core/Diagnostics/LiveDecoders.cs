using System.Threading;

namespace Flower.Diagnostics
{
    // How many track decoders are alive: constructed, and not yet closed. The
    // question a decode leak raises is "how many decoders are there", and the
    // thread count could never answer it - a decoder's thread is a dedicated
    // one and FFmpeg's are native, and neither is in the pool the runtime
    // counts. A decoder whose thread will not stop is deliberately leaked
    // rather than closed underneath it (FfmpegTrackDecoder.Retire), and stays
    // counted here, which is the point.
    public static class LiveDecoders
    {
        private static int _count;

        public static int Count => Volatile.Read(ref _count);

        public static void Opened() => Interlocked.Increment(ref _count);

        public static void Closed() => Interlocked.Decrement(ref _count);
    }
}
