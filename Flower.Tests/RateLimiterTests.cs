using System;

using Flower.Services;

namespace Flower.Tests;

public class RateLimiterTests
{
    [Fact]
    public void TryAcquire_allows_up_to_the_configured_max_within_the_window()
    {
        var limiter = new RateLimiter(max: 3, TimeSpan.FromSeconds(60));
        var now = DateTimeOffset.UtcNow;

        Assert.True(limiter.TryAcquire("key", now));
        Assert.True(limiter.TryAcquire("key", now));
        Assert.True(limiter.TryAcquire("key", now));
        Assert.False(limiter.TryAcquire("key", now));
    }

    [Fact]
    public void TryAcquire_recovers_the_full_budget_once_two_windows_have_passed()
    {
        var limiter = new RateLimiter(max: 1, TimeSpan.FromSeconds(60));
        var now = DateTimeOffset.UtcNow;

        Assert.True(limiter.TryAcquire("key", now));
        Assert.False(limiter.TryAcquire("key", now));

        // Two windows, not one: with a sliding window the previous window's
        // count still counts, weighted by how much of it is still overlapped.
        Assert.True(limiter.TryAcquire("key", now.AddSeconds(121)));
    }

    [Fact]
    public void TryAcquire_recovers_gradually_across_the_window_boundary()
    {
        var limiter = new RateLimiter(max: 10, TimeSpan.FromSeconds(60));
        var now = DateTimeOffset.UtcNow;

        for (var i = 0; i < 10; i++)
        {
            Assert.True(limiter.TryAcquire("key", now));
        }

        // Just past the boundary, essentially all of the spent budget still
        // counts.
        Assert.False(limiter.TryAcquire("key", now.AddSeconds(61)));
        // 95% through it, only ~0.5 of it does, so there is room again.
        Assert.True(limiter.TryAcquire("key", now.AddSeconds(117)));
    }

    [Fact]
    public void TryAcquire_does_not_let_a_boundary_burst_through_at_double_the_ceiling()
    {
        // The whole reason this is a sliding window (ARCHITECTURE-REVIEW Tier
        // 3.5): a fixed window admitted max requests in the last instant of
        // one window plus max more in the first instant of the next, i.e. 2x
        // the ceiling inside a span shorter than the window itself.
        var limiter = new RateLimiter(max: 5, TimeSpan.FromSeconds(60));
        var start = DateTimeOffset.UtcNow;
        var justBeforeBoundary = start.AddSeconds(59);
        var justAfterBoundary = start.AddSeconds(61);

        var allowed = 0;
        for (var i = 0; i < 5; i++)
        {
            if (limiter.TryAcquire("key", justBeforeBoundary))
                allowed++;
        }
        for (var i = 0; i < 5; i++)
        {
            if (limiter.TryAcquire("key", justAfterBoundary))
                allowed++;
        }

        Assert.Equal(5, allowed);
    }

    [Fact]
    public void TryAcquire_tracks_independent_budgets_per_key()
    {
        var limiter = new RateLimiter(max: 1, TimeSpan.FromSeconds(60));
        var now = DateTimeOffset.UtcNow;

        Assert.True(limiter.TryAcquire("key-a", now));
        Assert.True(limiter.TryAcquire("key-b", now));
        Assert.False(limiter.TryAcquire("key-a", now));
        Assert.False(limiter.TryAcquire("key-b", now));
    }

    [Fact]
    public void WouldAllow_reports_the_budget_without_spending_any_of_it()
    {
        var limiter = new RateLimiter(max: 2, TimeSpan.FromSeconds(60));
        var now = DateTimeOffset.UtcNow;

        // Peeking is what SubsonicEndpoints does on every request against its
        // failed-auth budget - if it charged, a single well-behaved client
        // would lock itself out.
        Assert.True(limiter.WouldAllow("key", now));
        Assert.True(limiter.WouldAllow("key", now));

        Assert.True(limiter.TryAcquire("key", now));
        Assert.True(limiter.TryAcquire("key", now));
        Assert.False(limiter.WouldAllow("key", now));
    }

    [Fact]
    public void Idle_keys_are_evicted_so_the_key_space_cannot_grow_without_bound()
    {
        // Keys are source IPs on every pre-trust endpoint, i.e. attacker-
        // chosen: without eviction a spoofed-source flood grows the
        // dictionary for free.
        var limiter = new RateLimiter(max: 1, TimeSpan.FromSeconds(60));
        var now = DateTimeOffset.UtcNow;

        Assert.True(limiter.TryAcquire("stale", now));

        Assert.Equal(1, limiter.TrackedKeyCount);

        // Any later call runs the sweep; five windows on is well past the
        // four-window idle cutoff.
        var later = now.AddSeconds(60 * 5);
        Assert.True(limiter.TryAcquire("other", later));
        Assert.Equal(1, limiter.TrackedKeyCount);
    }
}
