using System.Collections.Generic;
using System.Linq;
using System.Windows.Input;

using CommunityToolkit.Mvvm.Input;

using Flower.Models;
using Flower.Services;

namespace Flower.ViewModels.Mobile;

// ── Queue ─────────────────────────────────────────────────────────────
//
// The Queue tab: what is playing, and what plays after it. It is the queue
// PlaylistControl walks (CurrentPlaylist), not a copy of it, so a tap on a row
// is a jump along it rather than a new queue - see PlayTrackCommand - and a
// drag rearranges what is to come, the way it does a playlist.
//
// Also where songs are put into it from anywhere else: Play Next and Add to
// Queue, on a song's menu, on an album's, artist's or playlist's, and on a
// drilled-in screen's own header menu.
public partial class MobileMainViewModel
{
    public ICommand PlayNextActionTargetCommand { get; private set; } = null!;
    public ICommand AddActionTargetToQueueCommand { get; private set; } = null!;
    public ICommand PlayNextAlbumActionTargetCommand { get; private set; } = null!;
    public ICommand AddAlbumActionTargetToQueueCommand { get; private set; } = null!;
    public ICommand RemoveActionTargetFromQueueCommand { get; private set; } = null!;

    private void InitializeQueueCommands()
    {
        PlayNextActionTargetCommand = new RelayCommand(() => LineUpAndClose(ActionTarget is { } track ? [track] : [], next: true));
        AddActionTargetToQueueCommand = new RelayCommand(() => LineUpAndClose(ActionTarget is { } track ? [track] : [], next: false));
        PlayNextAlbumActionTargetCommand = new RelayCommand(() => LineUpAndClose(AlbumActionTargetTracks(), next: true));
        AddAlbumActionTargetToQueueCommand = new RelayCommand(() => LineUpAndClose(AlbumActionTargetTracks(), next: false));
        RemoveActionTargetFromQueueCommand = new RelayCommand(() =>
        {
            var slot = _actionRow is { } row ? QueueIndexOf(row) : -1;
            ActiveSheet = MobileSheet.None;
            if (slot >= 0)
                PlaylistControl.RemoveQueueEntry(slot);
        });
    }

    // The songs an album menu stands for, in the order they would play: a
    // playlist's own order when it was opened over a playlist, and album order
    // otherwise (see InAlbumOrder). Everything on that menu that plays, queues
    // or copies them goes through here - Play and Add to Playlist once sorted
    // a playlist by album, because the tile standing for it is an album's.
    private IReadOnlyList<Track> AlbumActionTargetTracks() =>
        PlaylistActionTarget?.Playlist is { } playlist ? playlist.Tracks.ToList()
        : AlbumActionTarget is { } tile ? InAlbumOrder(tile)
        : [];

    // Closes the menu first, so the queue it lands in is what shows.
    private void LineUpAndClose(IReadOnlyList<Track> tracks, bool next)
    {
        ActiveSheet = MobileSheet.None;
        LineUp(tracks, next);
    }

    private void LineUp(IReadOnlyList<Track> tracks, bool next)
    {
        if (next)
            PlaylistControl.PlayNext(tracks);
        else
            PlaylistControl.AddToQueue(tracks);
    }

    /// <summary>
    /// Whether the song the track menu was opened on can be taken out of the
    /// queue: opened from the Queue, and not on the song playing.
    /// </summary>
    public bool CanRemoveActionTargetFromQueue =>
        IsShowingQueue && _actionRow is { } row && QueueIndexOf(row) is var slot and >= 0
        && slot != PlaylistControl.QueueIndex;

    /// <summary>
    /// Whether the Queue's rows can be dragged into another order. Not under
    /// shuffle: the list is then every song the next one could be picked from,
    /// in no order that means anything, and a drag would pretend otherwise.
    /// </summary>
    public bool IsQueueReorderable => !PlaylistControl.IsShuffleEnabled;

    /// <summary>
    /// Moves <paramref name="dragged"/> to before <paramref name="insertBefore"/>,
    /// or to the end of the queue when that is null. Rows rather than tracks,
    /// because the queue can hold one song twice.
    /// </summary>
    public void ReorderQueueRow(TrackRowViewModel dragged, TrackRowViewModel? insertBefore)
    {
        var from = QueueIndexOf(dragged);
        if (from < 0)
            return;
        var to = insertBefore == null ? PlaylistControl.CurrentPlaylist.Tracks.Count : QueueIndexOf(insertBefore);
        if (to < 0)
            return;
        PlaylistControl.MoveQueueEntry(from, to);
    }

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
        : PlaylistControl.IsShuffleEnabled && PlaylistControl.LinedUpCount > 0
            ? "Shuffle is on: the songs you queued play next, then the rest at random."
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
        OnPropertyChanged(nameof(IsQueueReorderable));
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
    // them, after the ones lined up by hand, which are next whatever shuffle
    // says. Playing something outside the queue shows the whole queue.
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
                var linedUpEnd = System.Math.Min(queue.Count, current + 1 + PlaylistControl.LinedUpCount);
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

        var rows = TrackListBuilder.Build(tracks, null, "PlaylistOrder", true, playing,
            pairedServerFingerprint: Main.PairedServerFingerprint,
            pairedServerReachable: Main.IsPairedServerReachable);

        // The builder marks every copy of the playing song; here only its
        // own slot is the one playing.
        // Nor can it be dragged: everything else is queued behind it.
        for (var i = 0; i < rows.Count; i++)
        {
            rows[i].IsCurrentlyPlaying = i == 0 && current >= 0;
            rows[i].CanBeDragged = !rows[i].IsCurrentlyPlaying;
        }

        _queueSlots = slots;
        QueueRows = rows;
    }
}
