using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading;
using System.Windows.Input;

using Avalonia.Threading;

using CommunityToolkit.Mvvm.Input;

using Flower.Models;
using Flower.Services;
using Flower.ViewModels.Mobile;

namespace Flower.ViewModels;

// One album on the Continue Playing shelf, as a tile draws it: the cover, how
// far through the album the tape is, and what is left.
public sealed class ContinuePlayingItemViewModel : ViewModelBase
{
    public ContinuePlayingItemViewModel(ContinuePlayingShelfItem item, HomeViewModel home)
    {
        Item = item;
        Home = home;
    }

    // What the tile's commands are on. Reachable from the item itself because
    // desktop's right-click menu opens in a popup of its own, outside the
    // visual tree a binding would otherwise climb to find it.
    public HomeViewModel Home { get; }

    public ContinuePlayingShelfItem Item { get; private set; }

    public AlbumTileViewModel Tile => Item.Tile;

    // 0 to 1, across the whole album rather than the one song - the bar under
    // the cover is the tape, not the track.
    public double Progress =>
        Item.Total > TimeSpan.Zero ? Math.Clamp(Item.Elapsed / Item.Total, 0, 1) : 0;

    public string Detail => $"Song {Item.TrackIndex + 1} of {Item.Tracks.Count} · {Remaining(Item.Total - Item.Elapsed)}";

    internal void Update(ContinuePlayingShelfItem item)
    {
        Item = item;
        OnPropertyChanged(nameof(Item));
        OnPropertyChanged(nameof(Progress));
        OnPropertyChanged(nameof(Detail));
    }

    private static string Remaining(TimeSpan left)
    {
        if (left < TimeSpan.FromMinutes(1))
            return "under a minute left";

        var minutes = (int)Math.Round(left.TotalMinutes);
        return minutes < 60 ? $"{minutes} min left" : $"{minutes / 60} hr {minutes % 60} min left";
    }
}

// The Home screen, on every head: what the user was part-way through, what
// they played lately, and what arrived lately - three short shelves rather
// than three whole-library views, which is what Albums (sortable by date
// added) and History are for. See HomeShelves for what goes on each, and the
// three settings below for how much: so many albums on the first, so many
// songs on the second, so many months back on the third
// (AppSettings.HomeContinuePlayingLimit and friends, pushed in by
// MainViewModel).
//
// Filter narrows what the shelves show, the way the page filter narrows any
// other screen - the desktop's header box, the phone's pull-down oval.
//
// Built only while something is showing it: every song that starts changes
// Recently Played, and grouping the whole library into albums for a screen
// nobody is looking at is work for nothing. A change while hidden marks it
// stale, and it catches up when it is next shown - see IsActive.
public sealed class HomeViewModel : ViewModelBase, IDisposable
{
    // The settings' ceilings. Continue Playing cannot show more albums than
    // the tracker remembers; Recently Played's songs are held to the same
    // number so the two shelves offer the same range.
    public const int MaxAlbumsPerShelf = AlbumProgressTracker.MaxEntries;
    public const int MaxRecentlyAddedMonths = 24;

    private readonly Library _library;
    private readonly AlbumProgressTracker _progress;
    private readonly PlaylistControlViewModel? _playback;
    private readonly SubscriptionBag _subscriptions = new();
    private readonly TimeProvider _clock;

    // Whether the paired server is there to stream from, which decides
    // whether a cover of songs not on this device is greyed out.
    private readonly Func<(string? Fingerprint, bool Reachable)> _availability;

    public ObservableCollection<ContinuePlayingItemViewModel> ContinuePlaying { get; } = new();
    // Songs, as rows: their cover, what they are, the download and the
    // playing mark, as a song list's rows have them.
    public ObservableCollection<TrackRowViewModel> RecentlyPlayed { get; } = new();
    public ObservableCollection<AlbumTileViewModel> RecentlyAdded { get; } = new();

    public bool HasContinuePlaying => ContinuePlaying.Count > 0;
    public bool HasRecentlyPlayed => RecentlyPlayed.Count > 0;
    public bool HasRecentlyAdded => RecentlyAdded.Count > 0;
    public bool IsEmpty => !HasContinuePlaying && !HasRecentlyPlayed && !HasRecentlyAdded;

    // Which empty state the screen shows: nothing on Home at all, or nothing
    // on it the filter lets through.
    public bool ShowsNothingYet => IsEmpty && _filter == null;
    public bool ShowsNoMatches => IsEmpty && _filter != null;

    private int _continuePlayingLimit = 6;
    public int ContinuePlayingLimit
    {
        get => _continuePlayingLimit;
        set => SetSetting(ref _continuePlayingLimit, Math.Clamp(value, 0, MaxAlbumsPerShelf));
    }

    private int _recentlyPlayedLimit = 8;
    public int RecentlyPlayedLimit
    {
        get => _recentlyPlayedLimit;
        set => SetSetting(ref _recentlyPlayedLimit, Math.Clamp(value, 0, MaxAlbumsPerShelf));
    }

    private int _recentlyAddedMonths = 3;
    public int RecentlyAddedMonths
    {
        get => _recentlyAddedMonths;
        set => SetSetting(ref _recentlyAddedMonths, Math.Clamp(value, 0, MaxRecentlyAddedMonths));
    }

    // Top to bottom (HomeShelfOrder). Only where the shelves go, not what is
    // on them, so a change re-lays the rows without rebuilding a shelf.
    private IReadOnlyList<HomeShelf> _shelfOrder = HomeShelfOrder.Default;
    public IReadOnlyList<HomeShelf> ShelfOrder
    {
        get => _shelfOrder;
        set
        {
            if (value.SequenceEqual(_shelfOrder))
                return;
            _shelfOrder = value;
            ShelvesChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    // Trimmed, and null for nothing, so a box holding a stray space filters
    // nothing out.
    private string? _filter;
    public string? Filter
    {
        get => _filter;
        set => SetSetting(ref _filter, string.IsNullOrWhiteSpace(value) ? null : value.Trim());
    }

    private void SetSetting<T>(ref T field, T value)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
            return;
        field = value;
        Invalidate();
    }

    // Picks the album up where it was left.
    public ICommand ResumeCommand { get; }

    // Takes it off the Continue Playing shelf without playing it.
    public ICommand ForgetCommand { get; }

    // Plays the Recently Played list from that song down, as a song picked
    // in History plays the list it is in.
    public ICommand PlayRecentlyPlayedCommand { get; }

    // Song id to the last time it had started when it was taken off Recently
    // Played (HideFromRecentlyPlayed) - the shelf is the play history, so a
    // hidden song that is played again has earned its place back. Remembered
    // in settings (AppSettings.HomeRecentlyPlayedHidden, saved by
    // MainViewModel on HiddenFromRecentlyPlayedChanged).
    private Dictionary<string, DateTimeOffset> _hiddenFromRecentlyPlayed = new();
    public IReadOnlyDictionary<string, DateTimeOffset> HiddenFromRecentlyPlayed
    {
        get => _hiddenFromRecentlyPlayed;
        set
        {
            _hiddenFromRecentlyPlayed = new Dictionary<string, DateTimeOffset>(value);
            Invalidate();
        }
    }

    public event EventHandler? HiddenFromRecentlyPlayedChanged;

    // A Continue Playing or Recently Added cover tapped - where that goes is
    // the head's to decide (an album screen on a phone, the Albums grid with it
    // open on desktop), so it is handed back up.
    public ICommand OpenAlbumCommand { get; }
    public event EventHandler<AlbumTileViewModel>? AlbumOpened;

    // Recently Played's "See full history" - the whole play history, which is
    // the head's History page.
    public ICommand ShowHistoryCommand { get; }
    public event EventHandler? HistoryRequested;

    public HomeViewModel(Library library, AlbumProgressTracker progress, PlaylistControlViewModel? playback,
        Func<(string? Fingerprint, bool Reachable)>? availability = null, TimeProvider? clock = null)
    {
        _availability = availability ?? (() => (null, false));
        _clock = clock ?? TimeProvider.System;
        _library = library;
        _progress = progress;
        _playback = playback;

        ResumeCommand = new RelayCommand<ContinuePlayingItemViewModel>(Resume);
        ForgetCommand = new RelayCommand<ContinuePlayingItemViewModel>(item =>
        {
            if (item != null)
                _progress.Forget(item.Item.Entry.AlbumId);
        });
        PlayRecentlyPlayedCommand = new RelayCommand<TrackRowViewModel>(row =>
        {
            if (row != null)
                PlayRecentlyPlayed(row);
        });
        ShowHistoryCommand = new RelayCommand(() => HistoryRequested?.Invoke(this, EventArgs.Empty));
        OpenAlbumCommand = new RelayCommand<AlbumTileViewModel>(tile =>
        {
            if (tile != null)
                AlbumOpened?.Invoke(this, tile);
        });

        _subscriptions.Add<EventHandler>((_, _) => Invalidate(),
            h => library.LibraryChanged += h, h => library.LibraryChanged -= h);
        // A song starting moves Recently Played, and moves the playing mark
        // along it; a tag edit, new art or a download changes what a tile or
        // a song says or shows. Stars, options and play counts change nothing
        // here.
        _subscriptions.Add<EventHandler<TrackChangedEventArgs>>((_, e) =>
        {
            if ((e.Change & (TrackChange.LastPlayed | TrackChange.Reshaping)) != 0)
                Invalidate();
        },
            h => library.TrackChanged += h, h => library.TrackChanged -= h);
        _subscriptions.Add<EventHandler>((_, _) => Invalidate(),
            h => progress.Changed += h, h => progress.Changed -= h);
    }

    public void Dispose()
    {
        _subscriptions.Dispose();
        _holdTimer?.Dispose();
        foreach (var row in RecentlyPlayed)
            row.Dispose();
    }

    // An album picked up from the shelf stays where it was clicked for a
    // while before moving to the front, which playing it puts it at in the
    // tracker straight away. Moving it at once takes the tile out from under
    // the pointer that just pressed it, and the shelf rearranging itself is the
    // only answer the click gets. Then it moves, and HomeShelfRowsHost animates
    // the covers into their new places.
    public static readonly TimeSpan ResumedAlbumHold = TimeSpan.FromSeconds(10);

    private string? _heldAlbumId;
    private int _heldIndex;
    private ITimer? _holdTimer;

    private void HoldInPlace(ContinuePlayingItemViewModel item)
    {
        _holdTimer?.Dispose();
        _holdTimer = null;
        _heldAlbumId = null;

        var index = ContinuePlaying.IndexOf(item);
        if (index <= 0)
            return;

        _heldAlbumId = item.Item.Entry.AlbumId;
        _heldIndex = index;
        _holdTimer = _clock.CreateTimer(_ => Dispatcher.UIThread.Post(ReleaseHold), null, ResumedAlbumHold, Timeout.InfiniteTimeSpan);
    }

    private void ReleaseHold()
    {
        _holdTimer?.Dispose();
        _holdTimer = null;
        if (_heldAlbumId == null)
            return;

        _heldAlbumId = null;
        Invalidate();
    }

    // The held album put back at the place it was clicked in, wherever the
    // tracker has it now.
    private List<ContinuePlayingShelfItem> ApplyHold(List<ContinuePlayingShelfItem> items)
    {
        if (_heldAlbumId is not { } held)
            return items;

        var at = items.FindIndex(i => i.Entry.AlbumId == held);
        if (at < 0)
            return items;

        var item = items[at];
        items.RemoveAt(at);
        items.Insert(Math.Min(_heldIndex, items.Count), item);
        return items;
    }

    // Whether Home is on screen - set by the head as it navigates.
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
                ScheduleRebuild();
            if (value)
                Shown?.Invoke(this, EventArgs.Empty);
        }
    }

    // Home came on screen - the moment a shelf moved on another device is
    // worth fetching rather than waiting for (see AlbumProgressSyncService).
    public event EventHandler? Shown;

    private void Invalidate()
    {
        _isStale = true;
        if (_isActive)
            ScheduleRebuild();
    }

    // Coalesced: a sync landing can raise a TrackChanged per track, and one
    // rebuild after the burst says everything a hundred would.
    private int _rebuildScheduled;

    private void ScheduleRebuild()
    {
        if (Interlocked.Exchange(ref _rebuildScheduled, 1) == 1)
            return;

        Dispatcher.UIThread.Post(() =>
        {
            Interlocked.Exchange(ref _rebuildScheduled, 0);
            Rebuild();
        }, DispatcherPriority.Background);
    }

    public void Rebuild()
    {
        _isStale = false;
        var snapshot = _library.Snapshot;

        var continuing = HomeShelves.ContinuePlaying(_progress.Entries, snapshot, _continuePlayingLimit);
        var played = HomeShelves.RecentlyPlayed(snapshot, _recentlyPlayedLimit, _hiddenFromRecentlyPlayed);
        var added = _recentlyAddedMonths == 0
            ? []
            : HomeShelves.RecentlyAdded(snapshot, _clock.GetLocalNow().AddMonths(-_recentlyAddedMonths));

        // After the limits, so the filter narrows what the shelves show
        // rather than reaching past them for more.
        if (_filter is { } filter)
        {
            continuing = continuing.Where(c => FilterableAlbumGrid.Matches(c.Tile, filter)).ToList();
            played = played.Where(t => TrackListBuilder.Matches(t, filter)).ToList();
            added = added.Where(t => FilterableAlbumGrid.Matches(t, filter)).ToList();
        }

        SetContinuePlaying(ApplyHold(continuing));
        SetSongs(played);
        SetTiles(RecentlyAdded, added);
        ApplyAvailability();

        OnPropertyChanged(nameof(HasContinuePlaying));
        OnPropertyChanged(nameof(HasRecentlyPlayed));
        OnPropertyChanged(nameof(HasRecentlyAdded));
        OnPropertyChanged(nameof(IsEmpty));
        OnPropertyChanged(nameof(ShowsNothingYet));
        OnPropertyChanged(nameof(ShowsNoMatches));
        ShelvesChanged?.Invoke(this, EventArgs.Empty);
    }

    // After every rebuild - what a view laying the shelves out in rows of its
    // own (HomeShelfRowsHost) re-chunks on.
    public event EventHandler? ShelvesChanged;

    // Greys out a cover none of whose songs can be played right now, as the
    // album grids do.
    public void ApplyAvailability()
    {
        var (pairedServerFingerprint, reachable) = _availability();
        TrackAvailability.Apply(ContinuePlaying.Select(c => c.Tile), pairedServerFingerprint, reachable);
        TrackAvailability.Apply(RecentlyPlayed.ToList(), pairedServerFingerprint, reachable);
        TrackAvailability.Apply(RecentlyAdded, pairedServerFingerprint, reachable);
    }

    // Is this album on the Continue Playing shelf - for a menu offering to
    // take it off.
    public bool IsContinuing(AlbumTileViewModel tile) => AlbumIdOf(tile) is { } id && _progress.Entries.Any(e => e.AlbumId == id);

    public void Forget(AlbumTileViewModel tile)
    {
        if (AlbumIdOf(tile) is { } id)
            _progress.Forget(id);
    }

    public void HideFromRecentlyPlayed(Track track)
    {
        if (track.LastPlayedAt is not { } lastPlayed)
            return;

        // The song's own last play rather than the time now, so a play
        // recorded on another device's clock is compared with plays, not with
        // this one. Songs since played again, or gone from the library, have
        // nothing left to hide and are dropped on the way.
        var playedAt = _library.Tracks.ToDictionary(t => t.Id.ToKey(), t => t.LastPlayedAt);
        foreach (var (songId, at) in _hiddenFromRecentlyPlayed.ToList())
        {
            if (!playedAt.TryGetValue(songId, out var last) || last > at)
                _hiddenFromRecentlyPlayed.Remove(songId);
        }

        _hiddenFromRecentlyPlayed[track.Id.ToKey()] = lastPlayed;
        HiddenFromRecentlyPlayedChanged?.Invoke(this, EventArgs.Empty);
        Invalidate();
    }

    // The shelf as the queue, from the picked song down - false for a row
    // that is not one of the shelf's.
    public bool PlayRecentlyPlayed(TrackRowViewModel row)
    {
        var index = RecentlyPlayed.IndexOf(row);
        if (index < 0 || _playback == null)
            return false;
        _playback.PlayQueueFrom(RecentlyPlayed.Select(r => r.Track).ToList(), index, TimeSpan.Zero);
        return true;
    }

    // Null for a tile that is not one album - the album menu is also raised
    // over a playlist and over every album of an artist, and neither of those
    // is on the shelf even when its first song's album is.
    private static string? AlbumIdOf(AlbumTileViewModel tile)
    {
        if (tile.Tracks.Count == 0 || AlbumProgressTracker.AlbumIdOf(tile.Tracks[0]) is not { } id)
            return null;

        return tile.Tracks.All(t => AlbumProgressTracker.AlbumIdOf(t) == id) ? id : null;
    }

    private void Resume(ContinuePlayingItemViewModel? item)
    {
        if (item == null || _playback == null)
            return;

        var shelf = item.Item;
        HoldInPlace(item);

        // The album already playing, or paused where it is: the tape is in the
        // deck, so this carries on rather than rewinding to the last place it
        // was written down.
        if (_playback.CurrentlyPlayingTrack?.Id == shelf.Entry.TrackId)
        {
            if (!_playback.IsPlaying)
                _playback.PlayOrPause();
            return;
        }

        _playback.PlayQueueFrom(shelf.Tracks, shelf.TrackIndex, shelf.Entry.Position);
    }

    private void SetContinuePlaying(List<ContinuePlayingShelfItem> items)
    {
        // Reusing a tile keeps the cover it has already loaded; reusing the
        // item around it keeps the view from building the tile again at all
        // when all that moved is the bar under it.
        var tiles = AlbumTileMerge.Apply(ContinuePlaying.Select(c => c.Tile).ToList(), items.Select(i => i.Tile).ToList(), out var retired);
        var previous = ContinuePlaying.ToDictionary(c => c.Tile);

        var built = new List<ContinuePlayingItemViewModel>(items.Count);
        for (var i = 0; i < items.Count; i++)
        {
            var item = items[i] with { Tile = tiles[i] };
            if (previous.TryGetValue(tiles[i], out var existing))
            {
                existing.Update(item);
                built.Add(existing);
            }
            else
            {
                built.Add(new ContinuePlayingItemViewModel(item, this));
            }
        }

        Replace(ContinuePlaying, built);
        foreach (var tile in retired)
            tile.Dispose();
    }

    // A song still on the shelf keeps its row, and with it the cover it has
    // already loaded; the rows for songs gone from it are let go.
    private void SetSongs(List<Track> songs)
    {
        var (pairedServerFingerprint, reachable) = _availability();
        var playingId = _playback?.CurrentlyPlayingTrack?.Id;
        var previous = RecentlyPlayed.ToDictionary(r => r.Track);
        var rows = songs
            .Select(track =>
            {
                if (!previous.Remove(track, out var row))
                    row = new TrackRowViewModel { Track = track };
                row.IsCurrentlyPlaying = track.Id == playingId;
                return row;
            })
            .ToList();
        TrackAvailability.Apply(rows, pairedServerFingerprint, reachable);

        Replace(RecentlyPlayed, rows);
        foreach (var row in previous.Values)
            row.Dispose();
    }

    private static void SetTiles(ObservableCollection<AlbumTileViewModel> shelf, List<AlbumTileViewModel> built)
    {
        var tiles = AlbumTileMerge.Apply(shelf.ToList(), built, out var retired);
        Replace(shelf, tiles);
        foreach (var tile in retired)
            tile.Dispose();
    }

    // Left alone when nothing moved, which is most rebuilds: a song starting
    // on the album already at the front of Recently Played changes nothing a
    // shelf shows.
    private static void Replace<T>(ObservableCollection<T> shelf, IReadOnlyList<T> items) where T : class
    {
        if (shelf.Count == items.Count && shelf.Select((t, i) => ReferenceEquals(t, items[i])).All(same => same))
            return;

        shelf.Clear();
        foreach (var item in items)
            shelf.Add(item);
    }
}
