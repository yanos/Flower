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
    public static string Runtime(IEnumerable<Track> tracks)
    {
        var total = TimeSpan.FromTicks(tracks.Sum(t => t.Duration.Ticks));
        return total <= TimeSpan.Zero ? "" : DurationText.Compact(total);
    }

    public static string For(IReadOnlyCollection<Track> tracks)
    {
        var runtime = Runtime(tracks);
        return runtime.Length == 0 ? Songs(tracks.Count) : $"{Songs(tracks.Count)}  ·  {runtime}";
    }
}
