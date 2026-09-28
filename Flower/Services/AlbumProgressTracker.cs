using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

using Flower.Models;
using Flower.Persistence;

namespace Flower.Services;

// One album the user is part-way through: which of its songs the tape is at,
// and how far into it. AlbumId is CatalogIdentity's, the same (album artist,
// album) grouping every album view uses, so an entry survives a rescan that
// replaces every Track object - and TrackId is Track.Id, which does too.
public sealed record AlbumProgressEntry(string AlbumId, Guid TrackId, TimeSpan Position, DateTimeOffset UpdatedAt);

// An album taken off the shelf - played to the end, or removed by hand - and
// when. Kept for a while rather than forgotten at once so that the removal
// reaches the server and the listener's other devices, instead of being put
// back by the next of them that still has the album (see
// AlbumProgressSyncService, and AlbumProgressMerge for how long).
public sealed record AlbumProgressRemoval(string AlbumId, DateTimeOffset RemovedAt);

// What album-progress.json holds.
public sealed record AlbumProgressState(List<AlbumProgressEntry> Entries, List<AlbumProgressRemoval> Removals);

// Keeps the Home screen's "Continue Playing" shelf: albums started and not
// finished, each remembered at the exact place it was left, like a cassette.
//
// An album is started by two songs from it playing one straight after the
// other. One song is not enough - playing a single from the Songs list is not
// listening to its album - and two in a row is what an album being listened to
// looks like from in here whichever way it was put on: from its own screen,
// from the Songs list sorted by album, from a playlist that happens to hold two
// of its songs together. Not under shuffle, where two neighbours from one album
// are a coincidence.
//
// From then on every song of it that starts moves the tape, and the position
// is latched as it plays and written down whenever playback is put down - a
// pause, a stop, another song, the app going away. It is finished, and leaves
// the shelf, when its last song plays to the end. Anything short of that - a
// skip off the last song, another album put on half-way - leaves it where it
// was, which is the point: the shelf is for coming back to.
//
// Driven by PlaylistControlViewModel, which is where every start, end and pause
// already passes through - see its calls into this class. Kept in step with
// the listener's other devices by AlbumProgressSyncService, through
// MergeRemote: whichever device touched an album last says where it is.
public sealed class AlbumProgressTracker
{
    // How many albums the shelf remembers. More than Home shows (see
    // HomeViewModel.MaxContinuePlaying), so an album that drops off the end of
    // the shelf for being older can come back when one in front of it
    // finishes. The server keeps the same number (AlbumProgressMerge).
    public const int MaxEntries = AlbumProgressMerge.MaxLiveAlbums;

    private readonly Library _library;
    private readonly AlbumProgressStore? _store;
    private readonly ILogger _logger;
    private readonly Func<DateTimeOffset> _now;

    // Guards everything below: a song ending is reported on the decoder's
    // thread (PlaylistControlViewModel's EndReached handler), a sync's answer
    // on the pool, everything else on the UI thread.
    private readonly object _lock = new();

    // Most recently touched first.
    private List<AlbumProgressEntry> _entries;
    private readonly Dictionary<string, DateTimeOffset> _removals = new();

    // The song that started before the one playing - what "two in a row" is
    // measured against.
    private Track? _lastStarted;

    // The entry the playing song belongs to, if it belongs to one, and where
    // in that song playback has got to. The song as well as the album, since a
    // sync can move the album's tape somewhere else mid-song - another device
    // played it more recently - and this device's position in *this* song is
    // then no longer the album's.
    private string? _activeAlbumId;
    private Guid _activeTrackId;
    private long _activePositionMs;

    public AlbumProgressTracker(Library library, AlbumProgressStore? store = null,
        ILogger<AlbumProgressTracker>? logger = null, Func<DateTimeOffset>? now = null)
    {
        _library = library;
        _store = store;
        _logger = (ILogger?)logger ?? NullLogger.Instance;
        _now = now ?? (() => DateTimeOffset.UtcNow);

        var saved = store?.Load();
        _entries = saved?.Entries ?? new List<AlbumProgressEntry>();
        foreach (var removal in saved?.Removals ?? [])
            _removals[removal.AlbumId] = removal.RemovedAt;
    }

    // Raised whenever the shelf changes - an album joining or leaving it, or
    // one moving on - on whichever thread moved it, whether this device moved
    // it or another one did.
    public event EventHandler? Changed;

    // Raised only for a change made here, which is the one kind worth telling
    // the server about straight away. A change a sync brought in is already
    // the server's.
    public event EventHandler? LocallyChanged;

    public IReadOnlyList<AlbumProgressEntry> Entries
    {
        get
        {
            lock (_lock)
                return _entries.ToList();
        }
    }

    public IReadOnlyList<AlbumProgressRemoval> Removals
    {
        get
        {
            lock (_lock)
                return _removals.Select(r => new AlbumProgressRemoval(r.Key, r.Value)).ToList();
        }
    }

    // Null for a song with no album to be part-way through.
    public static string? AlbumIdOf(Track track) =>
        string.IsNullOrWhiteSpace(track.Album) ? null : CatalogIdentity.AlbumIdFor(track);

    public void TrackStarted(Track track, bool shuffling)
    {
        bool changed;
        lock (_lock)
        {
            // The song being left gets its place written down first, while
            // the latched position is still its own.
            changed = LeaveActiveTrack();

            var previous = _lastStarted;
            _lastStarted = track;
            changed |= Enter(track, previous, shuffling);
        }

        if (changed)
            ChangedHere();
    }

    // Whether the song starting belongs on the shelf, and if so puts its album
    // at the front with the tape at this song.
    private bool Enter(Track track, Track? previous, bool shuffling)
    {
        var albumId = AlbumIdOf(track);
        if (albumId == null)
            return false;

        var index = IndexOf(albumId);
        TimeSpan position;
        if (index >= 0)
        {
            // Starting the very song the tape is at is picking up where it
            // was left - Continue Playing itself, which seeks there - so the
            // place survives until playback reports a new one.
            var existing = _entries[index];
            position = existing.TrackId == track.Id ? existing.Position : TimeSpan.Zero;
            _entries.RemoveAt(index);
        }
        else if (!shuffling && previous != null && previous.Id != track.Id && AlbumIdOf(previous) == albumId)
        {
            position = TimeSpan.Zero;
            _logger.LogInformation("Remembering where {Album} is up to", track.Album);
        }
        else
        {
            return false;
        }

        _entries.Insert(0, new AlbumProgressEntry(albumId, track.Id, position, _now()));
        _removals.Remove(albumId);
        if (_entries.Count > MaxEntries)
            _entries.RemoveRange(MaxEntries, _entries.Count - MaxEntries);

        _activeAlbumId = albumId;
        _activeTrackId = track.Id;
        Interlocked.Exchange(ref _activePositionMs, (long)position.TotalMilliseconds);
        return true;
    }

    // Latched as often as the audio manager reports it, since by the time a
    // stop is heard about the position is gone (see IAudioManager.Time).
    public void UpdatePosition(long timeMs)
    {
        if (timeMs > 0)
            Interlocked.Exchange(ref _activePositionMs, timeMs);
    }

    // A song played all the way through. The last one finishes the album;
    // any other one moves the tape to the top of the next.
    public void TrackFinished(Track track)
    {
        lock (_lock)
        {
            var albumId = AlbumIdOf(track);
            var index = albumId == null ? -1 : IndexOf(albumId);
            if (albumId == null || index < 0)
                return;

            var entry = _entries[index];
            if (entry.TrackId != track.Id)
                return;

            if (_activeAlbumId == albumId)
                _activeAlbumId = null;

            var tracks = _library.Snapshot.AlbumTracks(albumId);
            var at = IndexOf(tracks, track.Id);
            if (at < 0 || at == tracks.Count - 1)
            {
                _logger.LogInformation("{Album} played to the end", track.Album);
                RemoveAt(index);
            }
            else
            {
                _entries[index] = entry with { TrackId = tracks[at + 1].Id, Position = TimeSpan.Zero, UpdatedAt = _now() };
            }
        }

        ChangedHere();
    }

    // Playback put down - paused, stopped, or the app going away. Writes down
    // where the playing song got to without letting go of it, since a pause
    // is followed by more of the same song.
    public void Flush(bool synchronously = false)
    {
        bool changed;
        lock (_lock)
        {
            changed = WriteActivePosition();
        }

        if (!changed)
            return;

        if (synchronously)
            SaveNow();
        else
            Persist();
        Changed?.Invoke(this, EventArgs.Empty);
        LocallyChanged?.Invoke(this, EventArgs.Empty);
    }

    // Taken off the shelf by hand.
    public void Forget(string albumId)
    {
        lock (_lock)
        {
            var index = IndexOf(albumId);
            if (index < 0)
                return;
            RemoveAt(index);
            if (_activeAlbumId == albumId)
                _activeAlbumId = null;
        }

        ChangedHere();
    }

    // What another of the listener's devices did, by way of the server:
    // albums they moved on, and albums they took off. Per album, whichever
    // side touched it last wins; a tie stays as it is here, so hearing back
    // what this device just said changes nothing.
    public void MergeRemote(IEnumerable<AlbumProgressEntry> entries, IEnumerable<AlbumProgressRemoval> removals)
    {
        var changed = false;
        lock (_lock)
        {
            // Anything still latched is newer than what is about to be
            // compared against, and has to be in the comparison.
            changed = WriteActivePosition();

            foreach (var remote in entries)
            {
                if (remote.UpdatedAt <= LastTouched(remote.AlbumId))
                    continue;

                var index = IndexOf(remote.AlbumId);
                if (index >= 0)
                    _entries.RemoveAt(index);
                _entries.Add(remote);
                _removals.Remove(remote.AlbumId);
                if (_activeAlbumId == remote.AlbumId && _activeTrackId != remote.TrackId)
                    _activeAlbumId = null;
                changed = true;
            }

            foreach (var remote in removals)
            {
                if (remote.RemovedAt <= LastTouched(remote.AlbumId))
                    continue;

                var index = IndexOf(remote.AlbumId);
                if (index >= 0)
                    _entries.RemoveAt(index);
                _removals[remote.AlbumId] = remote.RemovedAt;
                if (_activeAlbumId == remote.AlbumId)
                    _activeAlbumId = null;
                changed = true;
            }

            if (changed)
            {
                _entries = _entries.OrderByDescending(e => e.UpdatedAt).Take(MaxEntries).ToList();
                PruneRemovals();
            }
        }

        if (!changed)
            return;

        Persist();
        Changed?.Invoke(this, EventArgs.Empty);
    }

    private DateTimeOffset LastTouched(string albumId)
    {
        var index = IndexOf(albumId);
        if (index >= 0)
            return _entries[index].UpdatedAt;
        return _removals.TryGetValue(albumId, out var removedAt) ? removedAt : DateTimeOffset.MinValue;
    }

    private void RemoveAt(int index)
    {
        _removals[_entries[index].AlbumId] = _now();
        _entries.RemoveAt(index);
        PruneRemovals();
    }

    private void PruneRemovals()
    {
        var now = _now();
        foreach (var stale in _removals.Where(r => now - r.Value >= AlbumProgressMerge.RemovalLifetime).Select(r => r.Key).ToList())
            _removals.Remove(stale);

        if (_removals.Count > AlbumProgressMerge.MaxRemovals)
        {
            foreach (var oldest in _removals.OrderBy(r => r.Value).Take(_removals.Count - AlbumProgressMerge.MaxRemovals).Select(r => r.Key).ToList())
                _removals.Remove(oldest);
        }
    }

    private void ChangedHere()
    {
        Persist();
        Changed?.Invoke(this, EventArgs.Empty);
        LocallyChanged?.Invoke(this, EventArgs.Empty);
    }

    private bool LeaveActiveTrack()
    {
        var changed = WriteActivePosition();
        _activeAlbumId = null;
        Interlocked.Exchange(ref _activePositionMs, 0);
        return changed;
    }

    private bool WriteActivePosition()
    {
        var index = _activeAlbumId == null ? -1 : IndexOf(_activeAlbumId);
        if (index < 0)
            return false;

        var position = TimeSpan.FromMilliseconds(Interlocked.Read(ref _activePositionMs));
        var entry = _entries[index];
        if (entry.TrackId != _activeTrackId || entry.Position == position)
            return false;

        _entries[index] = entry with { Position = position, UpdatedAt = _now() };
        return true;
    }

    private int IndexOf(string albumId)
    {
        for (var i = 0; i < _entries.Count; i++)
        {
            if (_entries[i].AlbumId == albumId)
                return i;
        }

        return -1;
    }

    private static int IndexOf(IReadOnlyList<Track> tracks, Guid id)
    {
        for (var i = 0; i < tracks.Count; i++)
        {
            if (tracks[i].Id == id)
                return i;
        }

        return -1;
    }

    private AlbumProgressState Snapshot()
    {
        lock (_lock)
            return new AlbumProgressState(
                _entries.ToList(),
                _removals.Select(r => new AlbumProgressRemoval(r.Key, r.Value)).ToList());
    }

    // Each write takes the shelf as it is when its turn comes rather than as
    // it was when it was asked for, so however writes queue up behind each
    // other the last one on disk is the latest.
    private readonly SemaphoreSlim _saveLock = new(1, 1);

    private void Persist()
    {
        if (_store == null)
            return;

        _ = Task.Run(async () =>
        {
            await _saveLock.WaitAsync();
            try
            {
                await _store.SaveAsync(Snapshot());
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Could not save album progress");
            }
            finally
            {
                _saveLock.Release();
            }
        });
    }

    private void SaveNow()
    {
        if (_store == null)
            return;

        try
        {
            _store.Save(Snapshot());
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not save album progress");
        }
    }
}
