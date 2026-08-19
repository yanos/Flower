using System;
using System.Threading.Tasks;

using Microsoft.Extensions.Logging;

namespace Flower.Services;

public static class TaskExtensions
{
    // Fire-and-forget for synchronous entry points that have nothing to await
    // them - a command binding, a gesture callback.
    //
    // Writing `async void` at those call sites instead makes an exception
    // escape onto the thread pool with no handler: it is not routed to
    // TaskScheduler.UnobservedTaskException (that only observes faulted Tasks,
    // and an async void method has no Task), so it tears the process down.
    //
    // Meziantou.Framework.Threading's own Forget() fixes that, but observes the
    // fault *silently* - a failed sync or drill-in would leave no trace at all.
    // This overload is the same thing with the exception logged, which is what
    // we want on every path that currently uses it. OperationCanceledException
    // is the expected outcome of a restarted debounce or a screen navigating
    // away, so it is dropped without noise.
    //
    // ForgetAwaited is only entered when the task has not already completed
    // synchronously, so the common case allocates no state machine.
    public static void Forget(this Task task, ILogger logger, string description)
    {
        if (task.IsCompletedSuccessfully)
            return;

        _ = ForgetAwaited(task, logger, description);

        static async Task ForgetAwaited(Task task, ILogger logger, string description)
        {
            try
            {
                await task.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "{Description} failed", description);
            }
        }
    }
}
