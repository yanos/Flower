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
// added) and History are for. See HomeShelves for what goes on each.
//
// Built only while something is showing it: every song that starts changes
// Recently Played, and grouping the whole library into albums for a screen
// nobody is looking at is work for nothing. A change while hidden marks it
// stale, and it catches up when it is next shown - see IsActive.
public sealed class HomeViewModel : ViewModelBase, IDisposable
{
    public const int MaxContinuePlaying = 10;
    public const int MaxRecentlyPlayed = 12;
    public const int MaxRecentlyAdded = 12;

    private readonly Library _library;
    private readonly AlbumProgressTracker _progress;
    private readonly PlaylistControlViewModel? _playback;
    private readonly SubscriptionBag _subscriptions = new();

    // Whether the paired server is there to stream from, which decides
    // whether a cover of songs not on this device is greyed out.
    private readonly Func<(string? Fingerprint, bool Reachable)> _availability;

    public ObservableCollection<ContinuePlayingItemViewModel> ContinuePlaying { get; } = new();
    public ObservableCollection<AlbumTileViewModel> RecentlyPlayed { get; } = new();
    public ObservableCollection<AlbumTileViewModel> RecentlyAdded { get; } = new();

    public bool HasContinuePlaying => ContinuePlaying.Count > 0;
    public bool HasRecentlyPlayed => RecentlyPlayed.Count > 0;
    public bool HasRecentlyAdded => RecentlyAdded.Count > 0;
    public bool IsEmpty => !HasContinuePlaying && !HasRecentlyPlayed && !HasRecentlyAdded;

    // Picks the album up where it was left.
    public ICommand ResumeCommand { get; }

    // Takes it off the Continue Playing shelf without playing it.
    public ICommand ForgetCommand { get; }

    // A Recently Played or Recently Added cover tapped - where that goes is
    // the head's to decide (an album screen on a phone, the Albums grid with it
    // open on desktop), so it is handed back up.
    public ICommand OpenAlbumCommand { get; }
    public event EventHandler<AlbumTileViewModel>? AlbumOpened;

    public HomeViewModel(Library library, AlbumProgressTracker progress, PlaylistControlViewModel? playback,
        Func<(string? Fingerprint, bool Reachable)>? availability = null)
    {
        _availability = availability ?? (() => (null, false));
        _library = library;
        _progress = progress;
        _playback = playback;

        ResumeCommand = new RelayCommand<ContinuePlayingItemViewModel>(Resume);
        ForgetCommand = new RelayCommand<ContinuePlayingItemViewModel>(item =>
        {
            if (item != null)
                _progress.Forget(item.Item.Entry.AlbumId);
        });
        OpenAlbumCommand = new RelayCommand<AlbumTileViewModel>(tile =>
        {
            if (tile != null)
                AlbumOpened?.Invoke(this, tile);
        });

        _subscriptions.Add<EventHandler>((_, _) => Invalidate(),
            h => library.LibraryChanged += h, h => library.LibraryChanged -= h);
        // A song starting moves Recently Played; a tag edit, new art or a
        // download changes what a tile says or shows. Stars, options and play
        // counts change nothing here.
        _subscriptions.Add<EventHandler<TrackChangedEventArgs>>((_, e) =>
        {
            if ((e.Change & (TrackChange.PlayStarted | TrackChange.Reshaping)) != 0)
                Invalidate();
        },
            h => library.TrackChanged += h, h => library.TrackChanged -= h);
        _subscriptions.Add<EventHandler>((_, _) => Invalidate(),
            h => progress.Changed += h, h => progress.Changed -= h);
    }

    public void Dispose() => _subscriptions.Dispose();

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

        var continuing = HomeShelves.ContinuePlaying(_progress.Entries, snapshot, MaxContinuePlaying);
        var onShelf = continuing.Select(c => c.Entry.AlbumId).ToHashSet();

        SetContinuePlaying(continuing);
        SetTiles(RecentlyPlayed, HomeShelves.RecentlyPlayed(snapshot, onShelf, MaxRecentlyPlayed));
        SetTiles(RecentlyAdded, HomeShelves.RecentlyAdded(snapshot, MaxRecentlyAdded));
        ApplyAvailability();

        OnPropertyChanged(nameof(HasContinuePlaying));
        OnPropertyChanged(nameof(HasRecentlyPlayed));
        OnPropertyChanged(nameof(HasRecentlyAdded));
        OnPropertyChanged(nameof(IsEmpty));
    }

    // Greys out a cover none of whose songs can be played right now, as the
    // album grids do.
    public void ApplyAvailability()
    {
        var (pairedServerFingerprint, reachable) = _availability();
        TrackAvailability.Apply(ContinuePlaying.Select(c => c.Tile), pairedServerFingerprint, reachable);
        TrackAvailability.Apply(RecentlyPlayed, pairedServerFingerprint, reachable);
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
