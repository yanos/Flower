using System;

using Flower.Logging;
using Flower.Tests.TestSupport;

using Sentry;
using Sentry.Protocol;

using Xunit;

namespace Flower.Tests.Logging;

// How often one device reports the same problem: once an hour at most, with
// every occurrence in between counted into the next report - so how often a
// crash happens is known without a crash loop spending everyone's quota.
[Collection("PlatformDataDirectory")]
public class CrashThrottleTests : PinnedDataDirectory
{
    private static readonly DateTimeOffset Noon = new(2026, 11, 12, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void The_first_occurrence_is_reported_as_one()
    {
        Assert.Equal(1, CrashThrottle.Admit("IOException@Open", "1.0.0", Noon));
    }

    [Fact]
    public void A_repeat_within_the_hour_is_held_back()
    {
        CrashThrottle.Admit("IOException@Open", "1.0.0", Noon);

        Assert.Equal(0, CrashThrottle.Admit("IOException@Open", "1.0.0", Noon.AddMinutes(10)));
    }

    // Three held back, then the fourth goes out carrying all four.
    [Fact]
    public void The_next_report_after_the_hour_counts_every_occurrence_held_back()
    {
        CrashThrottle.Admit("IOException@Open", "1.0.0", Noon);
        CrashThrottle.Admit("IOException@Open", "1.0.0", Noon.AddMinutes(10));
        CrashThrottle.Admit("IOException@Open", "1.0.0", Noon.AddMinutes(20));
        CrashThrottle.Admit("IOException@Open", "1.0.0", Noon.AddMinutes(30));

        Assert.Equal(4, CrashThrottle.Admit("IOException@Open", "1.0.0", Noon.AddMinutes(61)));
    }

    [Fact]
    public void A_different_problem_is_reported_at_once()
    {
        CrashThrottle.Admit("IOException@Open", "1.0.0", Noon);

        Assert.Equal(1, CrashThrottle.Admit("NullReferenceException@Play", "1.0.0", Noon.AddMinutes(1)));
    }

    [Fact]
    public void A_new_version_starts_afresh()
    {
        CrashThrottle.Admit("IOException@Open", "1.0.0", Noon);

        Assert.Equal(1, CrashThrottle.Admit("IOException@Open", "1.0.1", Noon.AddMinutes(1)));
    }

    // Sentry lists a stack caller first; where it threw is at the end, and
    // that is what makes two crashes the same one.
    [Fact]
    public void Problems_are_told_apart_by_their_type_and_innermost_frames()
    {
        static SentryEvent Thrown(string type, params string[] frames)
        {
            var stack = new SentryStackTrace();
            foreach (var frame in frames)
                stack.Frames.Add(new SentryStackFrame { Function = frame });
            return new SentryEvent { SentryExceptions = [new SentryException { Type = type, Stacktrace = stack }] };
        }

        var first = CrashThrottle.Key(Thrown("System.IO.IOException", "Main", "Run", "Scan", "Open"));
        var sameOtherCaller = CrashThrottle.Key(Thrown("System.IO.IOException", "Timer", "Run", "Scan", "Open"));
        var other = CrashThrottle.Key(Thrown("System.IO.IOException", "Main", "Run", "Scan", "Write"));

        Assert.Equal(first, sameOtherCaller);
        Assert.NotEqual(first, other);
    }
}
