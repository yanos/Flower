using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

using Flower.Services;

namespace Flower.Models
{
    public class Playlist
    {
        // Stable across renames so sync can tell "same playlist, new name" apart
        // from "a different playlist" - see PlaylistSyncService. Generated once,
        // either freshly or restored from disk by PlaylistStore.
        public Guid Id { get; }

        private string _name;
        public string Name
        {
            get => _name;
            set
            {
                _name = value;
                Touch();
            }
        }

        // Copy-on-write, never mutated in place, for the same reason
        // Library.Playlists is: the save triggered by Changed runs on a
        // threadpool thread (App.axaml.cs) and enumerates this while the UI
        // thread may be adding a track to the very same playlist.
        private string? _comment;

        // Subsonic's playlist attributes, and the reason this model needed
        // widening at all: they are columns in Schema.Baseline, so while the client
        // had no field for them they could only be written from outside
        // Flower.Core - which is what forced the server to keep a second,
        // SQL-per-request view of the same playlists. With a field here, one
        // resident playlist serves both.
        public string? Comment
        {
            get => _comment;
            set
            {
                _comment = value;
                Touch();
            }
        }

        private bool _isPublic;
        public bool IsPublic
        {
            get => _isPublic;
            set
            {
                _isPublic = value;
                Touch();
            }
        }

        // Set once, at creation, and never touched again - so unlike the two
        // above it is not a Touch()ing property. Subsonic reports it; nothing
        // in Flower edits it.
        public DateTimeOffset CreatedAt { get; }

        private SmartPlaylistRules? _rules;

        // The query this playlist is, or null for an ordinary hand-picked one -
        // see docs/SMART-PLAYLIST-PLAN.md. Deliberately one nullable property
        // on Playlist rather than a SmartPlaylist subclass: Tracks stays the
        // materialized result either way, so the sidebar, playback,
        // PlaylistRepository, the Subsonic surface and the track-shipping half
        // of sync all keep working untouched. Only the write path is new.
        //
        // A Touch()ing property, unlike Materialize below: editing the rules is
        // a user editing the playlist, and it is the one thing about a smart
        // playlist that sync has to carry.
        public SmartPlaylistRules? Rules
        {
            get => _rules;
            set
            {
                _rules = value;
                Touch();
            }
        }

        public bool IsSmart => _rules != null;

        private string? _vibePrompt;

        // The sentence a Vibe Playlist is made from ("music for a rainy day"),
        // or null for every other kind - see docs/VIBE-PLAYLIST-PLAN.md,
        // "Model of a playlist". Like Rules, it is what syncs about the
        // playlist, and Tracks is the pool its host ranked from it. Unlike a
        // smart playlist's, that pool is the server's answer for a paired
        // device: a device that cannot listen to songs never ranks anything.
        //
        // A Touch()ing property: editing the sentence is the user editing the
        // playlist.
        public string? VibePrompt
        {
            get => _vibePrompt;
            set
            {
                _vibePrompt = value;
                Touch();
            }
        }

        private bool _vibeInstrumental;

        // The No vocals switch (SemanticAudioFilters). Kept apart from the
        // prompt, so a prompt that turned it on by saying "instrumental" can
        // be edited without losing it, and a playlist can turn it off again.
        public bool VibeInstrumental
        {
            get => _vibeInstrumental;
            set
            {
                if (_vibeInstrumental == value)
                    return;

                _vibeInstrumental = value;
                Touch();
            }
        }

        public bool IsVibe => _vibePrompt != null;

        // A folder: a row that holds other playlists rather than songs, the
        // way Music.app's are. A playlist rather than a type of its own so
        // that everything a playlist already has - an id stable across
        // renames, UpdatedAt, a listener, a row in playlists, a place in the
        // sync manifest and its deletes - a folder has without a second copy
        // of any of it. Its Tracks are always empty: what it plays is its
        // playlists' songs (PlaylistTree.TracksOf), never a list of its own.
        // Fixed at creation, like Listener - nothing turns one into the other.
        public bool IsFolder { get; }

        // A list of songs somebody picked: what a song can be added to, dropped
        // on, or dragged into a new order in. A smart playlist's songs are its
        // rules', a Vibe Playlist's its sentence's, and a folder has none, so
        // none of them is.
        public bool IsOrdinary => !IsSmart && !IsVibe && !IsFolder;

        private Guid? _parentId;

        // The folder this playlist sits in, or null at the top level. An id
        // rather than a reference, because a folder and what is in it reach a
        // device separately - in one manifest, but resolved one by one - and
        // because an id that names nothing has an answer that needs no repair:
        // a parent that is missing, not a folder, or inside this playlist
        // itself reads as the top level (PlaylistTree.ParentOf). So a folder
        // deleted on one device while another moved a playlist into it leaves
        // that playlist at the top level rather than nowhere.
        //
        // A Touch()ing property: moving a playlist is an edit of it, and the
        // one thing about it that sync has to carry for the move to reach
        // another device.
        public Guid? ParentId
        {
            get => _parentId;
            set
            {
                if (_parentId == value)
                    return;

                _parentId = value;
                Touch();
            }
        }

        private double? _sortKey;

        // Where this playlist stands among the others in its folder (or at
        // the top level), lowest first - set by dragging it in the sidebar
        // (PlaylistOrder.Place) and read by PlaylistTree.Flatten. A number of
        // its own rather than a place in a list, so that moving one playlist
        // is an edit of that playlist alone and travels the way a move into a
        // folder does; nothing stores a list of playlists in order. Null, for
        // a playlist nobody has placed, comes after every placed one, in the
        // order the library holds them.
        //
        // A Touch()ing property, like ParentId.
        public double? SortKey
        {
            get => _sortKey;
            set
            {
                if (_sortKey == value)
                    return;

                _sortKey = value;
                Touch();
            }
        }

        // Whose playlist this is on a server shared by several listeners - see
        // Listeners. Always Listeners.Owner on a client, whose playlists are
        // its own user's; on a server, the fingerprint of the non-admin device
        // that made it. Fixed for the life of the playlist: a listener's
        // playlists are only ever written through that listener's own pushes,
        // so nothing moves one between listeners.
        public string Listener { get; }

        // Copy-on-write, never mutated in place, for the same reason
        // Library.Playlists is: the save triggered by Changed runs on a
        // threadpool thread (App.axaml.cs) and enumerates this while the UI
        // thread may be adding a track to the very same playlist.
        private EntryList _entries;

        // The songs and, once something has asked (StoredEntries), a key per
        // entry. One object so that a reader on another thread never pairs
        // one edit's songs with another's keys.
        private sealed class EntryList(List<Track> tracks, List<long>? keys)
        {
            private List<long>? _keys = keys;

            public List<Track> Tracks { get; } = tracks;

            public List<long>? KnownKeys => Volatile.Read(ref _keys);

            public List<long> Keys => LazyInitializer.EnsureInitialized(
                ref _keys, () => Tracks.Select(_ => NewEntryKey()).ToList());
        }

        private static long _lastEntryKey;

        private static long NewEntryKey() => Interlocked.Increment(ref _lastEntryKey);

        // Read-only so that every mutation has to go through the methods below,
        // which are what bump UpdatedAt and raise Changed. It used to be a bare
        // List<Track>, and MainViewModel.ReorderPlaylistRow duly reached in
        // and did its own Remove()+Insert() - so a drag-reorder changed the
        // playlist without bumping UpdatedAt at all, and PlaylistSyncPlanner
        // (which decides "did this side change?" purely from UpdatedAt against
        // a per-peer baseline) never saw the reorder and never propagated it.
        public IReadOnlyList<Track> Tracks => _entries.Tracks;

        // Raised after any mutation that bumps UpdatedAt. Library subscribes to
        // every playlist it holds and relays this as Library.PlaylistsChanged,
        // which is what actually drives persistence - see Library.PlaylistsChanged
        // for why that has to happen here rather than at each UI call site.
        public event EventHandler? Changed;

        // Never backwards, and never standing still: an edit is always later
        // than the version it edited. UpdatedAt is compared across devices
        // (PlaylistSyncPlanner, and the server taking a push only when it is
        // newer), and a copy adopted from a device whose clock runs ahead
        // carries that device's time - so on a device whose clock runs behind,
        // "now" could be earlier than the playlist being edited, and the edit
        // read everywhere as no edit at all.
        private void Touch() => Touch(DateTimeOffset.UtcNow);

        private void Touch(DateTimeOffset now)
        {
            var next = UpdatedAt.AddMilliseconds(1);
            UpdatedAt = now > next ? now : next;
            Changed?.Invoke(this, EventArgs.Empty);
        }

        // Stamps this copy as an edit made after `other` - for a copy chosen
        // over another one that is newer on the clock, which is what keeping
        // your own version of a conflict is. Without it the choice is made
        // here and then undone by every device comparing timestamps.
        public void MarkEditedAfter(DateTimeOffset other)
        {
            if (UpdatedAt > other)
                return;

            var justAfter = other.AddMilliseconds(1);
            var now = DateTimeOffset.UtcNow;
            Touch(justAfter > now ? justAfter : now);
        }

        // Bumped on every mutation (rename, track add/remove/reorder). Sync uses
        // this against a per-peer last-synced baseline to tell which side(s)
        // changed since they last agreed - see PlaylistSyncPlanner.
        public DateTimeOffset UpdatedAt { get; private set; }

        public Playlist(string name, List<Track> tracks) : this(Guid.NewGuid(), name, tracks, DateTimeOffset.UtcNow)
        {
        }

        public static Playlist NewFolder(string name, Guid? parentId = null) =>
            new(Guid.NewGuid(), name, [], DateTimeOffset.UtcNow, isFolder: true, parentId: parentId);

        public Playlist(
            Guid id,
            string name,
            List<Track> tracks,
            DateTimeOffset updatedAt,
            string? comment = null,
            bool isPublic = false,
            DateTimeOffset? createdAt = null,
            SmartPlaylistRules? rules = null,
            string listener = Listeners.Owner,
            bool isFolder = false,
            Guid? parentId = null,
            double? sortKey = null,
            string? vibePrompt = null,
            bool vibeInstrumental = false,
            IReadOnlyList<AbsentEntry>? absent = null)
        {
            Id = id;
            Listener = listener;
            IsFolder = isFolder;
            _parentId = parentId;
            _sortKey = sortKey;
            _name = name;
            _comment = comment;
            _isPublic = isPublic;
            _rules = rules;
            _vibePrompt = vibePrompt;
            _vibeInstrumental = vibeInstrumental;
            CreatedAt = createdAt ?? updatedAt;
            // Defensive copy, matching Library's own constructor - callers can pass a
            // list they keep their own reference to (App.axaml.cs constructs
            // MainPlaylist directly from library.Tracks). Without this, ReplaceAll's
            // Clear()+AddRange() mutates that same underlying list in place, and
            // since ReplaceAll always runs immediately before Library.UpdateTracks
            // (both here and in RescanLibraryAsync), UpdateTracks would read its
            // own "previous" snapshot from a list that had *already* been overwritten
            // with the fresh (PlayCount/DateAdded/ImportedPlayCount-defaulted) data -
            // silently discarding whatever was actually there, every single rescan.
            _entries = new EntryList(new List<Track>(tracks), null);
            _absent = absent is { Count: > 0 } ? [.. absent] : [];
            UpdatedAt = updatedAt;
        }

        // An entry whose song is not in the library right now: a drive that is
        // unplugged, a NAS that did not mount, a folder taken out of Settings.
        // Not shown and not playable, and not gone either. It is kept through
        // every load and save, in its place (just after the song it followed,
        // or first when After is null), and becomes an ordinary entry again
        // the moment the library has the song back (RebindTracks). Only a
        // hand-picked playlist has any: a smart or Vibe playlist's songs are
        // worked out again.
        public readonly record struct AbsentEntry(Guid TrackId, Guid? After);

        // Copy-on-write like _entries, and written after it, so that a save
        // reading this first and _entries second (Slots) can at worst see an
        // entry in both, which it drops, and never in neither.
        private List<AbsentEntry> _absent;

        // Every entry in order, present or not: what is stored.
        public IEnumerable<Guid> StoredTrackIds() => Slots().Select(slot => slot.Id);

        // One entry of this playlist. A song in a playlist twice is two
        // entries with one TrackId, and Key is what tells them apart across
        // edits: it stays with its entry through moves and removals of others.
        // Only for this object and this run of the app, so a copy that arrived
        // by sync shares no keys with the one it replaced. 0 for an absent
        // entry, which has no key until its song is back.
        public readonly record struct StoredEntry(Guid TrackId, long Key);

        public List<StoredEntry> StoredEntries() =>
            Slots(withKeys: true).Select(slot => new StoredEntry(slot.Id, slot.Key)).ToList();

        private List<(Guid Id, Track? Track, long Key)> Slots(bool withKeys = false)
        {
            var absent = _absent;
            var entries = _entries;
            var tracks = entries.Tracks;
            var keys = withKeys ? entries.Keys : entries.KnownKeys;
            long KeyAt(int slot) => keys?[slot] ?? 0;

            var slots = new List<(Guid Id, Track? Track, long Key)>(tracks.Count + absent.Count);
            if (absent.Count == 0)
            {
                for (var i = 0; i < tracks.Count; i++)
                    slots.Add((tracks[i].Id, tracks[i], KeyAt(i)));
                return slots;
            }

            var present = tracks.Select(t => t.Id).ToHashSet();
            var waiting = absent.Where(e => !present.Contains(e.TrackId)).ToList();
            void Emit(Guid? after)
            {
                foreach (var entry in waiting.Where(e => e.After == after))
                    slots.Add((entry.TrackId, null, 0));
                waiting.RemoveAll(e => e.After == after);
            }

            Emit(null);
            for (var i = 0; i < tracks.Count; i++)
            {
                slots.Add((tracks[i].Id, tracks[i], KeyAt(i)));
                Emit(tracks[i].Id);
            }

            // What they followed has left the playlist since.
            foreach (var entry in waiting)
                slots.Add((entry.TrackId, null, 0));
            return slots;
        }

        private void SetSlots(List<(Guid Id, Track? Track, long Key)> slots)
        {
            var tracks = new List<Track>(slots.Count);
            var keys = _entries.KnownKeys == null ? null : new List<long>(slots.Count);
            var absent = new List<AbsentEntry>();
            Guid? after = null;
            foreach (var (id, track, key) in slots)
            {
                if (track != null)
                {
                    tracks.Add(track);
                    keys?.Add(key != 0 ? key : NewEntryKey());
                    after = id;
                }
                else
                {
                    absent.Add(new AbsentEntry(id, after));
                }
            }

            _entries = new EntryList(tracks, keys);
            _absent = absent;
        }

        // Entries have been removed: an absent entry that followed one of them
        // now follows whatever came before it, rather than falling to the end.
        private void SetTracksKeepingAbsentInPlace(List<Track> next, List<long>? nextKeys)
        {
            if (_absent.Count == 0)
            {
                _entries = new EntryList(next, nextKeys);
                return;
            }

            var remaining = next.GroupBy(t => t.Id).ToDictionary(g => g.Key, g => g.Count());
            var absent = new List<AbsentEntry>();
            Guid? after = null;
            foreach (var (id, track, _) in Slots())
            {
                if (track == null)
                {
                    absent.Add(new AbsentEntry(id, after));
                }
                else if (remaining.GetValueOrDefault(id) > 0)
                {
                    remaining[id]--;
                    after = id;
                }
            }

            _entries = new EntryList(next, nextKeys);
            _absent = absent;
        }

        // The entries and keys left once `leaves` says which slots go.
        private (List<Track> Tracks, List<long>? Keys) Without(Func<int, bool> leaves)
        {
            var entries = _entries;
            var known = entries.KnownKeys;
            var tracks = new List<Track>(entries.Tracks.Count);
            var keys = known == null ? null : new List<long>(known.Count);
            for (var i = 0; i < entries.Tracks.Count; i++)
            {
                if (leaves(i))
                    continue;

                tracks.Add(entries.Tracks[i]);
                keys?.Add(known![i]);
            }
            return (tracks, keys);
        }

        // A newer copy of this playlist taking over from `previous` (a sync
        // adopting the other side's). That copy was made by a device that could
        // not see these songs either, so its not listing them says nothing.
        internal void KeepAbsentFrom(Playlist previous)
        {
            if (!IsOrdinary || previous._absent.Count == 0)
                return;

            var mine = _absent.Select(e => e.TrackId).ToHashSet();
            var carried = previous._absent.Where(e => !mine.Contains(e.TrackId));
            _absent = [.. _absent, .. carried];
            SetSlots(Slots());
        }

        public void InsertTrack(int index, Track track)
        {
            var entries = _entries;
            var next = new List<Track>(entries.Tracks);
            next.Insert(index, track);
            List<long>? keys = null;
            if (entries.KnownKeys is { } known)
            {
                keys = new List<long>(known);
                keys.Insert(index, NewEntryKey());
            }
            _entries = new EntryList(next, keys);
            Touch();
        }

        public void AppendTrack(Track track)
        {
            var entries = _entries;
            var keys = entries.KnownKeys is { } known ? new List<long>(known) { NewEntryKey() } : null;
            _entries = new EntryList(new List<Track>(entries.Tracks) { track }, keys);
            Touch();
        }

        // The entries at these slots (indexes into Tracks), as one edit, or no
        // edit at all when none of them is a slot here. By slot rather than by
        // song because a playlist can hold one song twice, and a song cannot
        // say which of the two was meant. Returns whether anything went.
        public bool RemoveEntriesAt(IReadOnlyCollection<int> slots)
        {
            var count = _entries.Tracks.Count;
            var doomed = slots.Where(slot => slot >= 0 && slot < count).ToHashSet();
            if (doomed.Count == 0)
                return false;

            var (next, keys) = Without(doomed.Contains);
            SetTracksKeepingAbsentInPlace(next, keys);
            Touch();
            return true;
        }

        // Every entry for any of these tracks, as one edit - or no edit at all,
        // UpdatedAt untouched, when none of them is here. See
        // Library.RemoveTracks.
        public void RemoveTracks(IReadOnlySet<Guid> ids)
        {
            var tracks = _entries.Tracks;
            if (!tracks.Any(t => ids.Contains(t.Id)))
                return;

            _absent = _absent.Where(e => !ids.Contains(e.TrackId)).ToList();
            var (next, keys) = Without(slot => ids.Contains(tracks[slot].Id));
            SetTracksKeepingAbsentInPlace(next, keys);
            Touch();
        }

        public void ReplaceAll(List<Track> tracks)
        {
            _entries = new EntryList(new List<Track>(tracks), null);
            Touch();
        }

        // Drag-to-reorder: the entry at slot `from` moves to sit just before the
        // entry now at slot `insertBefore`, or to the end when that is
        // Tracks.Count. By slot for RemoveEntriesAt's reason. Returns false -
        // changing nothing, not even UpdatedAt - for a slot that is not here
        // or a drag that would not reorder anything (dropped onto itself, or
        // onto the entry already following it).
        //
        // That last group matters beyond tidiness. UpdatedAt is the entire
        // basis on which PlaylistSyncPlanner decides "did this side change?"
        // against its per-peer baseline, so bumping it here would manufacture a
        // sync-visible edit out of a drag that moved nothing - and a drag that
        // lands back where it started is the single most common way a
        // drag-reorder ends.
        public bool MoveEntry(int from, int insertBefore)
        {
            var entries = _entries;
            var tracks = entries.Tracks;
            if (from < 0 || from >= tracks.Count || insertBefore < 0 || insertBefore > tracks.Count)
                return false;

            var target = insertBefore > from ? insertBefore - 1 : insertBefore;
            if (target == from)
                return false;

            var next = new List<Track>(tracks);
            var moved = next[from];
            next.RemoveAt(from);
            next.Insert(target, moved);

            List<long>? keys = null;
            if (entries.KnownKeys is { } known)
            {
                keys = new List<long>(known);
                var key = keys[from];
                keys.RemoveAt(from);
                keys.Insert(target, key);
            }

            _entries = new EntryList(next, keys);
            Touch();
            return true;
        }

        // Swaps each entry for whichever Track instance now represents it,
        // without touching UpdatedAt - see Library.RebindPlaylistTracks, which
        // is the only caller and explains why. Not a mutation in the sense the
        // methods above are: same songs, same order, same playlist, just the
        // objects a rescan replaced underneath it.
        //
        // Also where an absent entry comes back: back is the library having
        // its id again, which is no more an edit than the rest of this.
        internal void RebindTracks(Func<Track, Track?> current, Func<Guid, Track?> byId)
        {
            var entries = _entries;
            var next = new List<Track>(entries.Tracks);
            for (var i = 0; i < next.Count; i++)
            {
                if (current(next[i]) is { } replacement)
                    next[i] = replacement;
            }
            _entries = new EntryList(next, entries.KnownKeys);

            if (_absent.Count == 0)
                return;

            var slots = Slots();
            for (var i = 0; i < slots.Count; i++)
            {
                if (slots[i].Track == null && byId(slots[i].Id) is { } back)
                    slots[i] = (slots[i].Id, back, 0);
            }
            SetSlots(slots);
        }

        // Installs the result of evaluating Rules, without touching UpdatedAt.
        // Returns whether the contents actually changed, so a recomputation
        // pass over every smart playlist can persist only the ones that moved.
        //
        // Not Touch()ing is the invariant the whole design rests on, and it is
        // the same one RebindTracks above relies on: PlaylistSyncPlanner decides
        // "did this side change?" purely from UpdatedAt against a per-peer
        // baseline, and a re-evaluation is not a user edit - the songs changed
        // because the library did. Bumping UpdatedAt here would manufacture a
        // sync-visible edit on both devices out of a rescan, every rescan, and
        // make a content conflict reachable for a playlist that has no content
        // of its own to conflict over.
        //
        // Public, unlike RebindTracks, because the recomputation pass that
        // drives it is not Library - see SMART-PLAYLIST-PLAN.md phase 3. A
        // Vibe Playlist's pool is installed the same way and for the same
        // reason (VibePlaylistPools, and a paired device taking its server's).
        public bool Materialize(IReadOnlyList<Track> tracks)
        {
            var current = _entries.Tracks;
            if (current.Count == tracks.Count)
            {
                var identical = true;
                for (var i = 0; i < tracks.Count; i++)
                {
                    if (ReferenceEquals(current[i], tracks[i]))
                        continue;

                    identical = false;
                    break;
                }

                if (identical)
                    return false;
            }

            _entries = new EntryList(new List<Track>(tracks), null);
            return true;
        }

        public Track? GetTrack(int index)
        {
            return Tracks.ElementAtOrDefault(index);
        }
    }
}
