using System.Collections.Generic;

using Flower.Services;

namespace Flower.ViewModels.Mobile;

// ── Queue ─────────────────────────────────────────────────────────────
//
// The Queue tab: what is playing, and what plays after it. It is the queue
// PlaylistControl walks (CurrentPlaylist), not a copy of it, so a tap on a row
// is a jump along it rather than a new queue - see PlayTrackCommand.
public partial class MobileMainViewModel
{
    private List<TrackRowViewModel> _queueRows = [];

    // Replaced whole rather than cleared and refilled, like
    // SmartPlaylistPreviewRows: the queue can be the whole library.
    public List<TrackRowViewModel> QueueRows
    {
        get => _queueRows;
        private set
        {
            _queueRows = value;
            OnPropertyChanged();
        }
    }

    // Each row's slot in CurrentPlaylist, since the rows start part-way into
    // it and a slot is what Play needs to tell two copies of one song apart.
    private List<int> _queueSlots = [];

    // Set by a change while the Queue is not on screen, so a phone playing
    // through a long album is not rebuilding a list nobody is looking at on
    // every track; RaiseNavigationChanged rebuilds it on the way in.
    private bool _queueRowsStale = true;

    // A line over the rows when what follows is not simply the next row
    // down - null otherwise.
    public string? QueueCaption =>
        PlaylistControl.IsRepeatEnabled ? "Repeat is on, so this song plays again."
        : PlaylistControl.IsShuffleEnabled ? "Shuffle is on, so the next song is picked from these at random."
        : null;

    private int QueueIndexOf(TrackRowViewModel row)
    {
        var index = _queueRows.IndexOf(row);
        return index >= 0 ? _queueSlots[index] : -1;
    }

    private void QueueChanged()
    {
        OnPropertyChanged(nameof(QueueCaption));
        if (IsShowingQueue)
        {
            RebuildQueueRows();
            RaiseEmptyStateChanged();
        }
        else
        {
            _queueRowsStale = true;
        }
    }

    // The song playing first, then the rest of the queue: in order from it
    // onward, or - under shuffle, where any of them could be next - all of
    // them. Playing something outside the queue shows the whole queue.
    // Nothing playing shows nothing: before the first play, the queue is only
    // the library in the order it was scanned, which is not anything the user
    // lined up.
    private void RebuildQueueRows()
    {
        _queueRowsStale = false;

        var queue = PlaylistControl.CurrentPlaylist.Tracks;
        var playing = PlaylistControl.CurrentlyPlayingTrack;
        var current = PlaylistControl.QueueIndex;

        var slots = new List<int>(queue.Count);
        if (playing != null && current >= 0)
        {
            slots.Add(current);
            if (PlaylistControl.IsShuffleEnabled)
            {
                for (var i = 0; i < queue.Count; i++)
                {
                    if (i != current)
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

        var tracks = new List<Models.Track>(slots.Count);
        foreach (var slot in slots)
            tracks.Add(queue[slot]);

        var rows = TrackListBuilder.Build(tracks, null, "PlaylistOrder", true, playing,
            pairedServerFingerprint: Main.PairedServerFingerprint,
            pairedServerReachable: Main.IsPairedServerReachable);

        // The builder marks every copy of the playing song; here only its
        // own slot is the one playing.
        for (var i = 0; i < rows.Count; i++)
            rows[i].IsCurrentlyPlaying = i == 0 && current >= 0;

        _queueSlots = slots;
        QueueRows = rows;
    }
}
