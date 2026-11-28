using System;
using System.Diagnostics;
using System.Threading.Tasks;

using Flower.Services;

using Xunit;

namespace Flower.Tests.AppShell;

// The window-closing wait: every task or the budget, whichever is first, and
// never a failure of its own - see BoundedWait for why it is not Task.Wait.
public class BoundedWaitTests
{
    [Fact]
    public void Returns_as_soon_as_every_task_has_finished()
    {
        var watch = Stopwatch.StartNew();

        Assert.True(BoundedWait.ForAll(TimeSpan.FromSeconds(10), Task.Delay(50, TestContext.Current.CancellationToken), Task.Delay(100, TestContext.Current.CancellationToken)));
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(5));
    }

    // No stopwatch: returning at all, and false, is what shows it gave up. A
    // bound on the elapsed time only failed when a CI simulator stalled.
    [Fact]
    public void Gives_up_once_the_budget_has_passed()
    {
        var never = new TaskCompletionSource().Task;

        Assert.False(BoundedWait.ForAll(TimeSpan.FromMilliseconds(200), never, never));
    }

    [Fact]
    public void A_task_that_failed_counts_as_finished_and_does_not_throw()
    {
        Assert.True(BoundedWait.ForAll(TimeSpan.FromSeconds(5),
            Task.FromException(new InvalidOperationException("server refused")), Task.CompletedTask));
    }

    [Fact]
    public void Nothing_to_wait_for_is_finished()
    {
        Assert.True(BoundedWait.ForAll(TimeSpan.FromSeconds(1)));
    }
}
