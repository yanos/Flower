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
}
