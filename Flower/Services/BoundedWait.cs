using System;
using System.Threading;
using System.Threading.Tasks;

namespace Flower.Services;

// Blocks the calling thread until every task has finished or the budget has
// passed, whichever comes first - for the few places that must block (a window
// closing, where the process may be gone before anything awaited resumes).
//
// Not Task.Wait, on purpose. A Task.Wait on a task that has not finished
// tells an attached debugger it is about to block on another thread
// (Debugger.NotifyOfCrossThreadDependency), and that notification has to take
// the runtime's thread-store lock. With Rider attached on macOS the UI thread
// once stopped right there on quit, in the notification, before the wait's own
// timeout had even started - the app froze for hours and logged nothing. A
// ManualResetEventSlim sends no such notification, so the budget is the only
// thing that can hold the caller.
//
// The tasks' own failures are observed and dropped: each is a best-effort
// message to a server, and one that failed has nobody left to tell.
public static class BoundedWait
{
    public static bool ForAll(TimeSpan budget, params Task[] tasks)
    {
        // Not disposed: the continuation may still set it after the budget
        // has run out and this has returned. It never allocates a kernel
        // handle (nothing asks for its WaitHandle), so the GC is enough.
        var done = new ManualResetEventSlim();
        Task.WhenAll(tasks).ContinueWith(static (all, state) =>
            {
                _ = all.Exception;
                ((ManualResetEventSlim)state!).Set();
            },
            done, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        return done.Wait(budget);
    }
}
