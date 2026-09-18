using System;

using Flower.ViewModels;

using Xunit;

namespace Flower.Tests;

// A playlist's header says how long it runs in words, rounded to the minute -
// the row in the picker keeps the clock form, which lines up with the songs'.
public class DurationTextTests
{
    [Theory]
    [InlineData(0, 0, 45, "45 seconds")]
    [InlineData(0, 0, 1, "1 second")]
    [InlineData(0, 0, 30, "30 seconds")]
    [InlineData(0, 1, 0, "1 minute")]
    [InlineData(0, 12, 29, "12 minutes")]
    [InlineData(0, 12, 30, "13 minutes")]
    [InlineData(1, 0, 0, "1 hour")]
    [InlineData(1, 13, 0, "1 hour 13 minutes")]
    [InlineData(2, 1, 10, "2 hours 1 minute")]
    [InlineData(1, 59, 40, "2 hours")]
    [InlineData(26, 5, 0, "26 hours 5 minutes")]
    public void Spoken_says_it_in_words(int hours, int minutes, int seconds, string expected) =>
        Assert.Equal(expected, DurationText.Spoken(new TimeSpan(hours, minutes, seconds)));
}
