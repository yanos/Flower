using System.Runtime.CompilerServices;
using System.Threading;

namespace Flower.Tests.TestSupport;

// xUnit runs test collections in parallel by default, and several of them
// (GaplessCoordinatorTests, GaplessAudioManagerTests, TrackDecoderTests,
// GaplessCoordinatorRealDecodeTests, ...) drive their own async/callback-
// driven code via SpinWait.SpinUntil - a busy-wait on the calling thread,
// not a real yield. With enough of those spinning concurrently, the CLR
// ThreadPool's default slow thread-injection rate (about one new thread per
// several hundred ms once the pool is saturated) can leave genuinely async
// continuations elsewhere in the same process - a Task.Run, an
// HttpListener.GetContextAsync() continuation, a LibVLC Media.Parse's async
// completion - waiting tens of seconds for a worker thread that never comes
// available in time, even though the actual work behind them takes
// milliseconds. Confirmed directly: StreamingNetworkOutageTests' real-
// HTTP-server tests passed in isolation every time but failed/hung for 45s+
// only when run as part of the full suite, and disappeared entirely once
// this raised the minimum pool size - full-suite runtime also dropped from
// up to 48s to about 1s in the same before/after comparison, meaning this
// starvation was silently inflating every test's wall-clock time, not just
// the ones that outright failed.
internal static class AssemblySetup
{
    [ModuleInitializer]
    public static void RaiseThreadPoolMinimumToAvoidStarvationUnderParallelTestExecution()
    {
        ThreadPool.SetMinThreads(100, 100);
    }
}
