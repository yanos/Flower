using System.Threading;

namespace Flower.Tests.TestSupport;

// The thread pool's state, for the message of a wait that ran out of time.
// The iOS leg's intermittent timeouts - a model download over an in-memory
// handler still going after 30 s, a Task.Run'd Play not started after 10 -
// all wait on pool threads, and AssemblySetup's raised minimum is the only
// thing known to stand between a parallel run and starvation. A message that
// says how many threads there were and how much work was queued tells the
// next failure apart: a starved pool, or code that is genuinely stuck.
public static class ThreadPoolSnapshot
{
    public static string Describe()
    {
        ThreadPool.GetMinThreads(out var minWorkers, out _);
        return $"pool: {ThreadPool.ThreadCount} threads (minimum {minWorkers}), {ThreadPool.PendingWorkItemCount} work items queued";
    }
}
