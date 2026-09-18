using System;

namespace Flower.ViewModels;

/// <summary>
/// How long something is, in the app's one format: <c>m:ss</c>, widening to
/// <c>h:mm:ss</c> once there is an hour to show. Every length the user reads -
/// a song's in the track list, a resume position, a playlist's total - goes
/// through here, so they can be read against each other.
/// </summary>
public static class DurationText
{
    public static string Compact(TimeSpan span) =>
        (int)span.TotalHours > 0 ? span.ToString(@"h\:mm\:ss") : span.ToString(@"m\:ss");

    /// <summary>
    /// The same length in words - "1 hour 13 minutes" - for a place with room
    /// to say it and nothing beside it to be read against: a playlist's own
    /// header and its menu, rather than its row in a list of them. Rounded to the minute,
    /// since nobody wants the seconds of an hour-long playlist; a length under
    /// one is the one case that keeps them, so a single short song is not
    /// "0 minutes" long.
    /// </summary>
    public static string Spoken(TimeSpan span)
    {
        if (span < TimeSpan.FromMinutes(1))
            return Unit((int)Math.Round(span.TotalSeconds, MidpointRounding.AwayFromZero), "second");

        var minutes = (int)Math.Round(span.TotalMinutes, MidpointRounding.AwayFromZero);

        var hours = minutes / 60;
        minutes %= 60;
        if (hours == 0)
            return Unit(minutes, "minute");
        return minutes == 0 ? Unit(hours, "hour") : $"{Unit(hours, "hour")} {Unit(minutes, "minute")}";
    }

    private static string Unit(int count, string name) => count == 1 ? $"1 {name}" : $"{count:N0} {name}s";
}
