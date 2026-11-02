using System;
using System.Collections.Generic;

namespace Flower.SemanticAudio;

// Which 10 s windows of a song are its clips (docs/VIBE-PLAYLIST-PLAN.md,
// "Clips").
//
// A song is cut once into windows from 0: window k covers [10k s, 10k + 10 s),
// and a final window shorter than 3 s is not part of the grid. Every clip
// setting only picks indices on that grid, which is the whole point of it:
// the same window is the same clip under every setting, so lowering the
// setting analyses nothing and raising it analyses only windows never
// analysed before. The selection is phase 0's (prototype.py), and has to stay
// it - the owner's listening was done on these windows.
public static class ClipGrid
{
    // The setting's value for every window of the song.
    public const int WholeSong = 0;

    public const int DefaultClipsPerSong = 6;

    // What the settings page offers.
    public static readonly IReadOnlyList<int> Settings = [3, 6, 12, WholeSong];

    public static bool IsValidSetting(int clipsPerSong) => clipsPerSong == WholeSong || clipsPerSong > 0;

    public static long SampleCount(TimeSpan duration) => (long)Math.Round(duration.TotalSeconds * Clap.SampleRate);

    // Full windows, plus a tail of 3 s or more. A song shorter than 3 s still
    // has window 0, repeat-padded, since it is the only audio there is.
    public static int WindowCount(long samples)
    {
        var full = samples / Clap.ClipSamples;
        var tail = samples % Clap.ClipSamples;
        var count = full + (tail >= Clap.MinimumTailSamples ? 1 : 0);
        return (int)Math.Max(count, 1);
    }

    public static int WindowCount(TimeSpan duration) => WindowCount(SampleCount(duration));

    // How far from its point a stored window may be and still stand in for
    // the one the point falls in, as a share of the spacing between points.
    // A lower setting's points sit at most a quarter of its spacing from a
    // higher one's (6's are halfway between 3's; 12's between 6's), so this
    // makes every lowering - 12 to 6, 6 to 3, 12 to 3 - a choice among clips
    // already analysed. Raising reuses less: a higher setting's points are
    // half its spacing from a lower one's, so most of them get windows of
    // their own.
    internal const double StandInShare = 0.3;

    // The windows a setting selects, in the order chosen. N clips are the
    // windows holding the points 0.05 + 0.9 * (i + 0.5) / N of the song: evenly
    // spread, skipping the first and last 5%, where intros and fade-outs are.
    // That is phase 0's choice exactly, and the owner's listening was done on
    // it.
    //
    // Given the windows already analysed (`stored`), a point whose own window
    // is not among them takes the nearest one that is, when that is close
    // enough (StandInShare): the owner asked that lowering the setting only
    // ignore clips, and with evenly spread points the 3 aren't a subset of
    // the 6. Two points on one window count once, so a short song gets fewer
    // clips.
    public static IReadOnlyList<int> Select(int windowCount, TimeSpan duration, int clipsPerSong, IReadOnlySet<int>? stored = null)
    {
        if (!IsValidSetting(clipsPerSong))
            throw new ArgumentOutOfRangeException(nameof(clipsPerSong), clipsPerSong, "Clips per song is a positive count or WholeSong.");

        if (clipsPerSong == WholeSong || windowCount <= 1)
        {
            var all = new int[Math.Max(windowCount, 0)];
            for (var k = 0; k < all.Length; k++)
                all[k] = k;
            return all;
        }

        var seconds = duration.TotalSeconds;
        var reach = StandInShare * 0.9 * seconds / clipsPerSong;
        var picked = new List<int>(clipsPerSong);
        for (var i = 0; i < clipsPerSong; i++)
        {
            var point = (0.05 + 0.9 * (i + 0.5) / clipsPerSong) * seconds;
            var k = Math.Clamp((int)Math.Floor(point / 10), 0, windowCount - 1);
            if (stored is { Count: > 0 } && !stored.Contains(k) && Nearest(stored, point, windowCount) is { } near
                && Distance(point, near) <= reach)
                k = near;

            if (!picked.Contains(k))
                picked.Add(k);
        }

        return picked;
    }

    // Seconds from a point to a window's span; zero inside it.
    private static double Distance(double point, int window) =>
        Math.Max(0, Math.Max(window * 10.0 - point, point - (window * 10.0 + 10)));

    // The stored window nearest the point, the earlier on a tie.
    private static int? Nearest(IReadOnlySet<int> stored, double point, int windowCount)
    {
        int? best = null;
        var bestDistance = double.MaxValue;
        foreach (var window in stored)
        {
            if (window < 0 || window >= windowCount)
                continue;

            var distance = Distance(point, window);
            if (distance < bestDistance || (distance == bestDistance && window < best))
            {
                best = window;
                bestDistance = distance;
            }
        }

        return best;
    }
}
