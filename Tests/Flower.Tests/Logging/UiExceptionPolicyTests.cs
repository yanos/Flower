using System;

using Flower.Logging;

using Xunit;

namespace Flower.Tests.Logging;

// One failed click should not stop the music; a runtime in trouble, or an app
// failing over and over, is better off crashing and being reported as a crash.
public class UiExceptionPolicyTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 22, 9, 0, 0, TimeSpan.Zero);

    [Fact]
    public void An_ordinary_exception_is_survived()
    {
        Assert.True(new UiExceptionPolicy().Survives(new InvalidOperationException(), Now));
    }

    [Fact]
    public void A_runtime_in_trouble_is_not()
    {
        var policy = new UiExceptionPolicy();

        Assert.False(policy.Survives(new OutOfMemoryException(), Now));
        Assert.False(policy.Survives(new InvalidProgramException(), Now));
    }

    [Fact]
    public void One_too_many_in_a_row_is_not()
    {
        var policy = new UiExceptionPolicy();
        for (var i = 0; i < UiExceptionPolicy.MaxInWindow; i++)
            Assert.True(policy.Survives(new InvalidOperationException(), Now));

        Assert.False(policy.Survives(new InvalidOperationException(), Now));
    }

    [Fact]
    public void The_count_starts_over_once_it_has_been_quiet()
    {
        var policy = new UiExceptionPolicy();
        for (var i = 0; i < UiExceptionPolicy.MaxInWindow; i++)
            policy.Survives(new InvalidOperationException(), Now);

        Assert.True(policy.Survives(new InvalidOperationException(), Now + UiExceptionPolicy.Window));
    }
}
