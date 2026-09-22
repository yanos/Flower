using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

using Flower.Models;
using Flower.Server.Services;

namespace Flower.Server.Tests;

// The coordinator runs two scans now, not one: the admin API's, and the
// startup scan Program.cs hands it so that the listener can open on the stored
// catalog instead of after a full scan of a NAS share. What that second caller
// needs and the first does not is a completion callback - it is how the
// smart-playlist refresher still gets to make its opening pass against the
// real catalog, which is the ordering the awaited version got for free.
//
// So these are about the callback's contract rather than about scanning: it
// runs whatever the scan did, it runs exactly once, and a caller that did not
// start the scan does not get called back. A callback that only fired on
// success would leave a server whose scan threw with no smart playlists at
// all, over a stored catalog that is sitting right there.
public class LibraryRescanCoordinatorTests
{
    // A scan fails by failing to resolve: the coordinator's first act is to
    // resolve LibraryImportService out of a fresh scope, and an empty provider
    // has none. That exercises the catch and the finally without needing a
    // library, a database, or a folder to scan.
    private static LibraryRescanCoordinator EmptyProviderCoordinator() =>
        new(new ServiceCollection().BuildServiceProvider().GetRequiredService<IServiceScopeFactory>(),
            new Library([]),
            NullLogger<LibraryRescanCoordinator>.Instance);

    [Fact]
    public async Task RunsTheCompletionCallbackWhenTheScanFails()
    {
        var coordinator = EmptyProviderCoordinator();
        var ran = new TaskCompletionSource();

        Assert.True(coordinator.TryStart(onCompleted: () => ran.TrySetResult()));

        await ran.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        Assert.NotNull(coordinator.LastError);
        Assert.NotNull(coordinator.LastCompletedAt);
    }

    // A throwing callback must not take down the task it runs on, and must not
    // leave the coordinator looking like a scan is still in flight - which
    // would wedge every later TryStart, including the admin API's.
    [Fact]
    public async Task SurvivesACallbackThatThrows()
    {
        var coordinator = EmptyProviderCoordinator();
        var ran = new TaskCompletionSource();

        coordinator.TryStart(onCompleted: () =>
        {
            ran.TrySetResult();
            throw new InvalidOperationException("post-rescan step blew up");
        });

        await ran.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        await WaitUntilIdle(coordinator);

        Assert.True(coordinator.TryStart());
    }

    // The guard that stops two importers over the same folders, now that the
    // scan most likely to still be running when somebody presses Rescan is the
    // startup one. The loser's callback must not run: it did not start this
    // scan, and firing it would run a "after the first rescan" step before a
    // rescan it has no relationship to.
    [Fact]
    public async Task RefusesASecondScanAndDoesNotCallItBack()
    {
        var release = new ManualResetEventSlim(false);
        var entered = new TaskCompletionSource();
        var coordinator = new LibraryRescanCoordinator(
            new BlockingScopeFactory(entered, release),
            new Library([]),
            NullLogger<LibraryRescanCoordinator>.Instance);

        Assert.True(coordinator.TryStart());
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        Assert.True(coordinator.IsRunning);

        var loserWasCalledBack = false;
        Assert.False(coordinator.TryStart(onCompleted: () => loserWasCalledBack = true));

        release.Set();
        await WaitUntilIdle(coordinator);
        Assert.False(loserWasCalledBack);
    }

    private static async Task WaitUntilIdle(LibraryRescanCoordinator coordinator)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
        while (coordinator.IsRunning && DateTime.UtcNow < deadline)
            await Task.Delay(20, TestContext.Current.CancellationToken);

        Assert.False(coordinator.IsRunning);
    }

    // Holds the scan inside CreateScope until released, so "a scan is running"
    // is a state the test controls rather than one it races.
    private sealed class BlockingScopeFactory(TaskCompletionSource entered, ManualResetEventSlim release)
        : IServiceScopeFactory
    {
        public IServiceScope CreateScope()
        {
            entered.TrySetResult();
            release.Wait(TimeSpan.FromSeconds(10));

            // Resolution then fails, which the coordinator reports as a failed
            // scan - this test is about the guard, not about the outcome.
            return new ServiceCollection().BuildServiceProvider().CreateScope();
        }
    }
}
