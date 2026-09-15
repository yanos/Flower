using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using Flower.Models;
using Flower.ViewModels;

namespace Flower.Services;

// What one row of the track list *should* be, computed without touching a
// view-model. TrackListBuilder.Plan produces these off the UI thread; the
// merge below is what turns them into (or applies them to) the bound
// TrackRowViewModels, on the UI thread where raising PropertyChanged is legal.
public readonly record struct TrackRowPlan(
    Track Track,
    bool IsFirstInAlbumGroup,
    int AlbumGroupSize,
    bool IsCurrentlyPlaying,
    bool IsAvailable,
    bool IsAlbumGroupUnavailable);

// Reconciles the previous row list against a freshly computed plan, reusing the
// TrackRowViewModel already showing a track rather than allocating a new one.
//
// Every rebuild - a search keystroke, a sort, a sidebar navigation, and above
// all the background rescan that fires on every launch - used to throw away and
// re-allocate all ~16k rows, whether or not anything about them had changed.
// That cost more than the allocation: each new row starts with AlbumArt unset,
// so the whole visible window re-entered AlbumArtLoader (one Task.Run per row)
// to arrive back at the bitmap the discarded row was already holding, and any
// per-row transient UI state - an in-flight download's spinner most visibly -
// was lost with the instance that owned it. See docs/ARCHITECTURE-REVIEW.md
// Tier 1.5, where this was the largest deferred item.
//
// Rows are matched on Track.Id, not on the Track reference: a rescan builds
// brand-new Track instances straight from file tags and Library.
// CarryForwardMutableState copies the old Id onto them, so reference equality
// would miss on every single rescan - precisely the case this exists for. The
// reused row is re-pointed at the *new* instance (see
// TrackRowViewModel.ApplyPlan); keeping the old one would leave the list
// holding a different object graph than Library.Tracks, which is the same
// defect as Tier 0.3's orphaned playlist tracks.
public static class TrackRowMerge
{
    // retired: the previous rows that no longer appear, and are therefore the
    // caller's to Dispose - a reused row must not be disposed, since it is
    // still bound and may still own a running spinner subscription.
    // clock: handed to every row this creates, so the download spinner runs on
    // the container's AnimationClock rather than reaching for the static
    // default. Null falls back to that default - see TrackRowViewModel.Clock.
    public static List<TrackRowViewModel> Apply(
        IReadOnlyList<TrackRowViewModel>? previous,
        IReadOnlyList<TrackRowPlan> plan,
        out List<TrackRowViewModel> retired,
        AnimationClock? clock = null)
    {
        retired = new List<TrackRowViewModel>();
        var result = new List<TrackRowViewModel>(plan.Count);

        Dictionary<Guid, TrackRowViewModel>? reusable = null;
        if (previous is { Count: > 0 })
        {
            reusable = new Dictionary<Guid, TrackRowViewModel>(previous.Count);
            foreach (var row in previous)
            {
                // A duplicate id means the same track appeared twice in the old
                // list (a playlist may legitimately hold it twice). Only the
                // first is a reuse candidate; the rest retire immediately
                // rather than being dropped on the floor undisposed.
                if (!reusable.TryAdd(row.Track.Id, row))
                    retired.Add(row);
            }
        }

        foreach (var entry in plan)
        {
            if (reusable != null && reusable.Remove(entry.Track.Id, out var reused))
            {
                reused.ApplyPlan(entry);
                result.Add(reused);
            }
            else
            {
                result.Add(TrackRowViewModel.FromPlan(entry, clock));
            }
        }

        if (reusable != null)
        {
            foreach (var row in reusable.Values)
                retired.Add(row);
        }

        return result;
    }

    // Brings `current` to `next` without replacing it, when `next` is the same
    // row instances in the same order or with exactly one of them relocated -
    // which is what a playlist drag-to-reorder produces. Returns false, leaving
    // `current` untouched, for anything else; the caller replaces the
    // collection as before.
    //
    // Replacing it is a Reset to whatever list is bound to it, and on mobile
    // that is a ListBox, which re-realizes every visible row: after a drop the
    // row sat in its old place for well over a second on a phone before
    // jumping. A single Move is one container. Larger permutations (a sort
    // change over the whole library) are deliberately not attempted - as a
    // run of Move events they would cost more than the Reset they replace.
    public static bool TryApplyInPlace(ObservableCollection<TrackRowViewModel> current, IReadOnlyList<TrackRowViewModel> next)
    {
        if (current.Count != next.Count)
            return false;

        var first = 0;
        while (first < next.Count && ReferenceEquals(current[first], next[first]))
            first++;
        if (first == next.Count)
            return true;

        var last = next.Count - 1;
        while (last > first && ReferenceEquals(current[last], next[last]))
            last--;
        // One position differing on its own cannot be a relocation of an
        // existing row - it is a different instance.
        if (last == first)
            return false;

        if (MovedDown(current, next, first, last))
        {
            current.Move(first, last);
            return true;
        }
        if (MovedUp(current, next, first, last))
        {
            current.Move(last, first);
            return true;
        }
        return false;
    }

    // current[first] now sits at `last`, and everything between shifted up one.
    private static bool MovedDown(IReadOnlyList<TrackRowViewModel> current, IReadOnlyList<TrackRowViewModel> next, int first, int last)
    {
        if (!ReferenceEquals(next[last], current[first]))
            return false;
        for (var i = first; i < last; i++)
        {
            if (!ReferenceEquals(next[i], current[i + 1]))
                return false;
        }
        return true;
    }

    // current[last] now sits at `first`, and everything between shifted down one.
    private static bool MovedUp(IReadOnlyList<TrackRowViewModel> current, IReadOnlyList<TrackRowViewModel> next, int first, int last)
    {
        if (!ReferenceEquals(next[first], current[last]))
            return false;
        for (var i = first + 1; i <= last; i++)
        {
            if (!ReferenceEquals(next[i], current[i - 1]))
                return false;
        }
        return true;
    }
}
