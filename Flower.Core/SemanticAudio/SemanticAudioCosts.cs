using System;
using System.Collections.Generic;

using Flower.Models;

namespace Flower.SemanticAudio;

// What one clip setting costs for this library: the clips it would keep, the
// space they take, and roughly how long listening to all of them takes here.
// Time is null until this host has measured itself.
public sealed record SemanticAudioCost(int ClipsPerSong, long Clips, long Bytes, TimeSpan? Time);

// The settings page's "6 clips: 95 MB, about 8 h" (docs/VIBE-PLAYLIST-PLAN.md,
// "The settings page"), for every choice it offers. Counted from the songs'
// own lengths on the same grid the analyzer cuts, so a library of short songs
// is told it pays less for "whole song" than one of long ones. The time is
// from scratch, at the clip rate the analyzer last measured on this host
// (SemanticAudioProgress.ClipsPerSecond); raising the setting costs only the
// clips not already kept, and lowering it nothing.
public static class SemanticAudioCosts
{
    public static IReadOnlyList<SemanticAudioCost> For(IEnumerable<Track> tracks, double? clipsPerSecond)
    {
        var clips = new long[ClipGrid.Settings.Count];
        foreach (var track in tracks)
        {
            if (track.Duration <= TimeSpan.Zero)
                continue;

            var windows = ClipGrid.WindowCount(track.Duration);
            for (var i = 0; i < clips.Length; i++)
                clips[i] += ClipGrid.Select(windows, track.Duration, ClipGrid.Settings[i]).Count;
        }

        var costs = new List<SemanticAudioCost>(clips.Length);
        for (var i = 0; i < clips.Length; i++)
        {
            TimeSpan? time = clipsPerSecond is > 0 ? TimeSpan.FromSeconds(clips[i] / clipsPerSecond.Value) : null;
            costs.Add(new SemanticAudioCost(ClipGrid.Settings[i], clips[i], clips[i] * Embedding.StoredBytes, time));
        }

        return costs;
    }
}
