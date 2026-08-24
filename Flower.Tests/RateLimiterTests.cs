using System;
using System.Net;

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

// What counts as "one caller" for a per-source budget. See
// docs/OPEN-INTERNET-REVIEW.md finding #3: keying on the full address string
// hands an IPv6 caller its whole /64 as free buckets, so the ceilings bound
// nothing anyone willing to rotate addresses cares about.
public class RateLimitKeyTests
{
    [Fact]
    public void An_IPv4_caller_is_keyed_by_its_own_address()
    {
        Assert.Equal("203.0.113.7", RateLimiter.KeyFor(IPAddress.Parse("203.0.113.7")));
        Assert.NotEqual(RateLimiter.KeyFor(IPAddress.Parse("203.0.113.7")),
                        RateLimiter.KeyFor(IPAddress.Parse("203.0.113.8")));
    }

    [Fact]
    public void Two_addresses_in_one_IPv6_slash_64_share_a_key()
    {
        // The whole finding: these are one allocation, and an attacker holding
        // it can mint 2^64 of them.
        Assert.Equal(RateLimiter.KeyFor(IPAddress.Parse("2001:db8:1:2::1")),
                     RateLimiter.KeyFor(IPAddress.Parse("2001:db8:1:2:dead:beef:cafe:f00d")));
    }

    [Fact]
    public void Different_IPv6_slash_64s_do_not_share_a_key()
    {
        Assert.NotEqual(RateLimiter.KeyFor(IPAddress.Parse("2001:db8:1:2::1")),
                        RateLimiter.KeyFor(IPAddress.Parse("2001:db8:1:3::1")));
    }

    // Kestrel hands out ::ffff:a.b.c.d for an IPv4 client on a dual-stack
    // socket. Keyed as IPv6 that collapses to ::ffff:0:0/64 - every IPv4 caller
    // on the internet in one bucket, which is worse than no limiting at all.
    [Fact]
    public void An_IPv4_mapped_address_is_keyed_as_the_IPv4_address_it_is()
    {
        Assert.Equal(RateLimiter.KeyFor(IPAddress.Parse("203.0.113.7")),
                     RateLimiter.KeyFor(IPAddress.Parse("::ffff:203.0.113.7")));
        Assert.NotEqual(RateLimiter.KeyFor(IPAddress.Parse("::ffff:203.0.113.7")),
                        RateLimiter.KeyFor(IPAddress.Parse("::ffff:203.0.113.8")));
    }

    // Nothing off-link can source an fe80:: address, so there is no rotation to
    // bound - and collapsing them would put every device on the LAN into one
    // bucket, which is the case these limiters exist to keep working.
    [Fact]
    public void Link_local_neighbours_keep_their_own_keys()
    {
        Assert.NotEqual(RateLimiter.KeyFor(IPAddress.Parse("fe80::1")),
                        RateLimiter.KeyFor(IPAddress.Parse("fe80::2")));
    }

    [Fact]
    public void A_caller_with_no_address_still_gets_a_key()
    {
        // HttpListener and Kestrel both hand back a nullable address, and a
        // throw on the rate-limit path would be a denial of service of its own.
        Assert.False(string.IsNullOrEmpty(RateLimiter.KeyFor(null)));
    }
}
