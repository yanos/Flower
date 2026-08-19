using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using Flower.Services;

using Microsoft.Extensions.Logging;

using Xunit;

namespace Flower.Tests;

// Forget is what every synchronous entry point that starts async work now goes
// through (docs/ARCHITECTURE-REVIEW.md, "async void on non-event-handler
// paths"), so the thing worth pinning is that a fault is *observed* - reaching
// the logger is the proof, since an unobserved one would instead surface
// arbitrarily later on the finalizer thread.
public class TaskExtensionsTests
{
    // Records what was logged without a logging framework in the way. Only
    // Log<TState> is implemented; nothing here calls the scope or filter side.
    private sealed class RecordingLogger : ILogger
    {
        public readonly List<(LogLevel Level, Exception? Exception)> Entries = new();

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel level, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter) => Entries.Add((level, exception));
    }

    [Fact]
    public async Task A_faulting_task_is_logged_at_Error_with_its_exception()
    {
        var logger = new RecordingLogger();
        var failed = new TaskCompletionSource();

        Failing().Forget(logger, "Something");

        await failed.Task.WaitAsync(TimeSpan.FromSeconds(5));
        // The logging happens in Forget's continuation, just after the body
        // above completes the TCS - so give that continuation a turn to run.
        await WaitUntil(() => logger.Entries.Count > 0);

        var entry = Assert.Single(logger.Entries);
        Assert.Equal(LogLevel.Error, entry.Level);
        Assert.IsType<InvalidOperationException>(entry.Exception);

        async Task Failing()
        {
            await Task.Yield();
            failed.SetResult();
            throw new InvalidOperationException("boom");
        }
    }

    // Cancellation is the expected outcome of a restarted debounce or a screen
    // navigating away, so it is observed but deliberately not logged.
    [Fact]
    public async Task A_cancelled_task_is_observed_without_logging()
    {
        var logger = new RecordingLogger();
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        var task = Task.FromCanceled(cts.Token);
        task.Forget(logger, "Something");

        await WaitUntil(() => task.IsCompleted);
        Assert.Empty(logger.Entries);
    }

    [Fact]
    public async Task A_succeeding_task_logs_nothing()
    {
        var logger = new RecordingLogger();

        Task.CompletedTask.Forget(logger, "Something");
        await Task.Delay(20);

        Assert.Empty(logger.Entries);
    }

    private static async Task WaitUntil(Func<bool> condition)
    {
        for (var i = 0; i < 500 && !condition(); i++)
            await Task.Delay(10);
    }
}
