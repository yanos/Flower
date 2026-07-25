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
    public void TryAcquire_resets_after_the_window_elapses()
    {
        var limiter = new RateLimiter(max: 1, TimeSpan.FromSeconds(60));
        var now = DateTimeOffset.UtcNow;

        Assert.True(limiter.TryAcquire("key", now));
        Assert.False(limiter.TryAcquire("key", now));

        var afterWindow = now.AddSeconds(61);
        Assert.True(limiter.TryAcquire("key", afterWindow));
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
}
