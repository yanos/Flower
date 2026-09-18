using System;
using System.Collections.Generic;
using System.Linq;

using Flower.Models;

namespace Flower.ViewModels;

/// <summary>
/// What a playlist is, in one line: how many songs and how long they run.
/// One place rather than one per site, so the phone's playlist rows and the
/// playlist screen's own header can only ever say it the same way.
/// </summary>
public static class PlaylistSummaryText
{
    public static string Songs(int count) => count == 1 ? "1 song" : $"{count:N0} songs";

    /// <summary>
    /// The total, written the way a song's own length is (see
    /// <see cref="DurationText"/>) - the same reading beside the same kind of
    /// number, in the width a phone row has to spare. Empty when there is
    /// nothing to measure: a placeholder track carries no duration until it
    /// has been downloaded, so a playlist of them is silent about its length
    /// rather than claiming to be 0:00 long.
    /// </summary>
    public static string Runtime(IEnumerable<Track> tracks) => Runtime(tracks, DurationText.Compact);

    public static string For(IReadOnlyCollection<Track> tracks) => Join(tracks, Runtime(tracks));

    /// <summary>
    /// The playlist screen's own header, which has the width a row does not
    /// and nothing beside it to line up with, so it says the total in words
    /// (<see cref="DurationText.Spoken"/>) - "2 hours 13 minutes" rather than
    /// "2:13:07". Same silence as the row about a length it cannot measure.
    /// </summary>
    public static string ForHeader(IReadOnlyCollection<Track> tracks) =>
        Join(tracks, Runtime(tracks, DurationText.Spoken));

    private static string Runtime(IEnumerable<Track> tracks, Func<TimeSpan, string> format)
    {
        var total = TimeSpan.FromTicks(tracks.Sum(t => t.Duration.Ticks));
        return total <= TimeSpan.Zero ? "" : format(total);
    }

    private static string Join(IReadOnlyCollection<Track> tracks, string runtime) =>
        runtime.Length == 0 ? Songs(tracks.Count) : $"{Songs(tracks.Count)}  ·  {runtime}";
}
