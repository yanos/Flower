using System.Collections.Generic;
using System.Linq;
using System.Windows.Input;

using CommunityToolkit.Mvvm.Input;

using Flower.Models;
using Flower.Services;

namespace Flower.ViewModels.Mobile;

// ── Queue ─────────────────────────────────────────────────────────────
//
// The Queue tab: what is playing, and what plays after it - the shared
// QueueViewModel (Main.Queue), whose rows the desktop's Queue sidebar row
// draws too. A tap on a row is a jump along it rather than a new queue - see
// PlayTrackCommand - and a drag rearranges what is to come.
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
            var row = _actionRow;
            ActiveSheet = MobileSheet.None;
            if (row != null)
                Main.Queue.Remove([row]);
        });
    }

    // The songs an album menu stands for, in the order they would play: a
    // playlist's own order when it was opened over a playlist, and album order
    // otherwise (see InAlbumOrder). Everything on that menu that plays, queues
    // or copies them goes through here - Play and Add to Playlist once sorted
    // a playlist by album, because the tile standing for it is an album's.
    private IReadOnlyList<Track> AlbumActionTargetTracks() =>
        PlaylistActionTarget?.Playlist is { } playlist ? PlaylistTree.TracksOf(playlist, Main.Library.Playlists).ToList()
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
        IsShowingQueue && _actionRow is { } row && Main.Queue.CanRemove(row);

    // The rest is the shared queue (QueueViewModel), which the desktop's Queue
    // sidebar row draws too - forwarded under the names this screen's views
    // and tests have always bound.
    public bool IsQueueReorderable => Main.Queue.IsReorderable;

    public void ReorderQueueRow(TrackRowViewModel dragged, TrackRowViewModel? insertBefore) =>
        Main.Queue.Reorder(dragged, insertBefore);

    public List<TrackRowViewModel> QueueRows => Main.Queue.Rows;

    // Where the song playing is in QueueRows: the screen opens on it.
    public int QueuePlayedCount => Main.Queue.PlayedCount;

    private int QueueIndexOf(TrackRowViewModel row) => Main.Queue.SlotOf(row);

    private void OnQueuePropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(QueueViewModel.Rows):
                OnPropertyChanged(nameof(QueueRows));
                if (IsShowingQueue)
                    RaiseEmptyStateChanged();
                break;
            case nameof(QueueViewModel.IsReorderable):
                OnPropertyChanged(nameof(IsQueueReorderable));
                break;
        }
    }
}
