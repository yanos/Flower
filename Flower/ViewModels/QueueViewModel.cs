using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;

using Flower.Models;
using Flower.Services;

namespace Flower.ViewModels;

// The queue, as a screen shows it, on every head: what is playing, and what
// plays after it - the phone's Queue tab and the desktop's Queue sidebar row
// both draw these rows. It is the queue PlaylistControl walks (CurrentPlaylist),
// not a copy of it, so playing a row is a jump along it rather than a new
// queue, and a drag rearranges what is to come, the way it does a playlist.
//
// Rows rather than tracks throughout, because the queue can hold one song
// twice: each row knows its slot in CurrentPlaylist (SlotOf).
//
// Built only while something is showing it, like Home: every song that starts
// moves the queue, and rebuilding a list nobody is looking at on every track
// of a long album is work for nothing. A change while hidden marks it stale,
// and it catches up when it is next shown - see IsActive.
public sealed class QueueViewModel : ViewModelBase, IDisposable
{
    private readonly PlaylistControlViewModel _playback;
    private readonly Func<(string? Fingerprint, bool Reachable)> _availability;
    private readonly SubscriptionBag _subscriptions = new();

    public QueueViewModel(PlaylistControlViewModel playback, Func<(string? Fingerprint, bool Reachable)>? availability = null)
    {
        _playback = playback;
        _availability = availability ?? (() => (null, false));

        _subscriptions.Add<PropertyChangedEventHandler>((_, e) =>
        {
            if (e.PropertyName is nameof(PlaylistControlViewModel.CurrentlyPlayingTrack)
                or nameof(PlaylistControlViewModel.CurrentPlaylist)
                or nameof(PlaylistControlViewModel.IsShuffleEnabled)
                or nameof(PlaylistControlViewModel.IsRepeatEnabled))
                Changed();
        },
            h => playback.PropertyChanged += h, h => playback.PropertyChanged -= h);
    }

    public void Dispose() => _subscriptions.Dispose();

    // Whether a queue screen is showing - set by the head as it navigates.
    private bool _isActive;
    private bool _isStale = true;

    public bool IsActive
    {
        get => _isActive;
        set
        {
            if (_isActive == value)
                return;
            _isActive = value;
            if (value && _isStale)
                Rebuild();
        }
    }

    private List<TrackRowViewModel> _rows = [];

    // Replaced whole rather than cleared and refilled: the queue can be the
    // whole library.
    public List<TrackRowViewModel> Rows
    {
        get => _rows;
        private set
        {
            _rows = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(IsEmpty));
        }
    }

    public bool IsEmpty => _rows.Count == 0;

    // What a queue screen says with nothing on it, on both heads.
    public const string EmptyTitle = "Nothing Queued";
    public const string EmptyMessage = "Play a song, and what comes after it shows here.";

    // Each row's slot in CurrentPlaylist, since the rows start part-way into
    // it and a slot is what Play needs to tell two copies of one song apart.
    private List<int> _slots = [];

    // A line over the rows when what follows is not simply the next row
    // down - null otherwise.
    public string? Caption =>
        _playback.IsRepeatEnabled ? "Repeat is on, so this song plays again."
        : _playback.IsShuffleEnabled && _playback.LinedUpCount > 0
            ? "Shuffle is on: the songs you queued play next, then the rest at random."
        : _playback.IsShuffleEnabled ? "Shuffle is on, so the next song is picked from these at random."
        : null;

    /// <summary>
    /// Whether the rows can be dragged into another order. Not under shuffle:
    /// the list is then every song the next one could be picked from, in no
    /// order that means anything, and a drag would pretend otherwise.
    /// </summary>
    public bool IsReorderable => !_playback.IsShuffleEnabled;

    /// <summary><paramref name="row"/>'s slot in the queue, or -1 for a row that is not one of these.</summary>
    public int SlotOf(TrackRowViewModel row)
    {
        var index = _rows.IndexOf(row);
        return index >= 0 ? _slots[index] : -1;
    }

    /// <summary>
    /// Whether <paramref name="row"/> can be taken out of the queue: one of its
    /// rows, and not the song playing.
    /// </summary>
    public bool CanRemove(TrackRowViewModel row) => SlotOf(row) is var slot and >= 0 && slot != _playback.QueueIndex;

    /// <summary>
    /// Takes <paramref name="rows"/> out of the queue, leaving the song
    /// playing where it is. Last slot first, so each removal leaves the slots
    /// still to go where they were.
    /// </summary>
    public void Remove(IEnumerable<TrackRowViewModel> rows)
    {
        var slots = rows.Where(CanRemove).Select(SlotOf).Distinct().OrderByDescending(s => s).ToList();
        foreach (var slot in slots)
            _playback.RemoveQueueEntry(slot);
    }

    /// <summary>
    /// Moves <paramref name="dragged"/> to before <paramref name="insertBefore"/>,
    /// or to the end of the queue when that is null.
    /// </summary>
    public void Reorder(TrackRowViewModel dragged, TrackRowViewModel? insertBefore)
    {
        var from = SlotOf(dragged);
        if (from < 0)
            return;
        var to = insertBefore == null ? _playback.CurrentPlaylist.Tracks.Count : SlotOf(insertBefore);
        if (to < 0)
            return;
        _playback.MoveQueueEntry(from, to);
    }

    /// <summary>
    /// Plays <paramref name="row"/> where it is in the queue - a jump along
    /// it, not a new queue. False for a row that is not one of these.
    /// </summary>
    public bool Play(TrackRowViewModel row)
    {
        if (SlotOf(row) is not (var slot and >= 0))
            return false;
        _playback.Play(row.Track, slot);
        return true;
    }

    // Greys out a row none of whose song can be played right now, as every
    // other list does.
    public void ApplyAvailability()
    {
        var (pairedServerFingerprint, reachable) = _availability();
        TrackAvailability.Apply(_rows, pairedServerFingerprint, reachable);
    }

    private void Changed()
    {
        OnPropertyChanged(nameof(Caption));
        OnPropertyChanged(nameof(IsReorderable));
        if (_isActive)
            Rebuild();
        else
            _isStale = true;
    }

    // The song playing first, then the rest of the queue: in order from it
    // onward, or - under shuffle, where any of them could be next - all of
    // them, after the ones lined up by hand, which are next whatever shuffle
    // says. Playing something outside the queue shows the whole queue.
    // Nothing playing shows nothing: before the first play, the queue is only
    // the library in the order it was scanned, which is not anything the user
    // lined up.
    public void Rebuild()
    {
        _isStale = false;

        var queue = _playback.CurrentPlaylist.Tracks;
        var playing = _playback.CurrentlyPlayingTrack;
        var current = _playback.QueueIndex;

        var slots = new List<int>(queue.Count);
        if (playing != null && current >= 0)
        {
            slots.Add(current);
            if (_playback.IsShuffleEnabled)
            {
                var linedUpEnd = Math.Min(queue.Count, current + 1 + _playback.LinedUpCount);
                for (var i = current + 1; i < linedUpEnd; i++)
                    slots.Add(i);
                for (var i = 0; i < queue.Count; i++)
                {
                    if (i != current && (i <= current || i >= linedUpEnd))
                        slots.Add(i);
                }
            }
            else
            {
                for (var i = current + 1; i < queue.Count; i++)
                    slots.Add(i);
            }
        }
        else if (playing != null)
        {
            for (var i = 0; i < queue.Count; i++)
                slots.Add(i);
        }

        var tracks = new List<Track>(slots.Count);
        foreach (var slot in slots)
            tracks.Add(queue[slot]);

        var (pairedServerFingerprint, reachable) = _availability();
        var rows = TrackListBuilder.Build(tracks, null, "PlaylistOrder", true, playing,
            pairedServerFingerprint: pairedServerFingerprint,
            pairedServerReachable: reachable);

        // The builder marks every copy of the playing song; here only its
        // own slot is the one playing.
        // Nor can it be dragged: everything else is queued behind it.
        for (var i = 0; i < rows.Count; i++)
        {
            rows[i].IsCurrentlyPlaying = i == 0 && current >= 0;
            rows[i].CanBeDragged = !rows[i].IsCurrentlyPlaying;
        }

        _slots = slots;
        Rows = rows;
    }
}
