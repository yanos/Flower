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

        // Every entry, and the songs among them this library has. One object
        // so that a reader on another thread never pairs one edit's songs with
        // another's entries. A list made from songs alone mints its entries
        // when something first asks: the queue is a Playlist too, rebuilt on
        // every play, and never needs them.
        private sealed class EntryList
        {
            private IReadOnlyList<PlaylistEntry>? _all;

            // Where each of Tracks sits in All, or null when they line up.
            private readonly int[]? _places;

            public IReadOnlyList<Track> Tracks { get; }

            public EntryList(List<Track> tracks) => Tracks = tracks;

            public EntryList(IReadOnlyList<PlaylistEntry> all)
            {
                _all = all;
                var tracks = new List<Track>(all.Count);
                var places = new List<int>(all.Count);
                for (var i = 0; i < all.Count; i++)
                {
                    if (all[i].Track is not { } track)
                        continue;

                    tracks.Add(track);
                    places.Add(i);
                }

                Tracks = tracks;
                _places = tracks.Count == all.Count ? null : [.. places];
            }

            public bool HasEntries => Volatile.Read(ref _all) != null;

            public IReadOnlyList<PlaylistEntry> All => LazyInitializer.EnsureInitialized(
                ref _all, () => (IReadOnlyList<PlaylistEntry>)Tracks.Select(PlaylistEntry.Of).ToList())!;

            public int UnavailableCount => HasEntries ? All.Count - Tracks.Count : 0;

            private IReadOnlyList<PlaylistEntry>? _shown;

            // The entries of Tracks, slot for slot.
            public IReadOnlyList<PlaylistEntry> Shown => LazyInitializer.EnsureInitialized(
                ref _shown, () => _places == null ? All : _places.Select(place => All[place]).ToList())!;

            // Where in All the song at this slot of Tracks is, or the end for
            // the slot just past the last.
            public int PlaceOf(int slot) =>
                slot == Tracks.Count ? All.Count : _places?[slot] ?? slot;
        }

        // Read-only so that every mutation has to go through the methods below,
        // which are what bump UpdatedAt and raise Changed - and so what saves
        // and syncs the edit.
        public IReadOnlyList<Track> Tracks => _entries.Tracks;

        // Raised after any mutation that bumps UpdatedAt. Library subscribes to
        // every playlist it holds and relays this as Library.PlaylistsChanged,
        // which is what actually drives persistence - see Library.PlaylistsChanged
        // for why that has to happen here rather than at each UI call site.
        public event EventHandler? Changed;

        // Never backwards, and never standing still: an edit is always later
        // than the version it edited. UpdatedAt is compared across devices
        // (which side's name or settings a merge keeps, and which copy wins
        // when only one may), and a copy adopted from a device whose clock runs ahead
        // carries that device's time - so on a device whose clock runs behind,
        // "now" could be earlier than the playlist being edited, and the edit
        // read everywhere as no edit at all.
        private void Touch() => Touch(DateTimeOffset.UtcNow);

        private void Touch(DateTimeOffset now)
        {
            lock (_clock)
            {
                var next = UpdatedAt.AddMilliseconds(1);
                UpdatedAt = now > next ? now : next;
            }
            Changed?.Invoke(this, EventArgs.Empty);
        }

        // Two edits on two threads each move the clock on, rather than one
        // overwriting the other's step.
        private readonly object _clock = new();

        // Every change to the rows goes through here: worked out from the list
        // as it is, and swapped in only if no other edit got there first -
        // worked out again if one did. Rows change on the UI thread, in a sync
        // session and in a large add running in the background (AppendTracks),
        // and a plain read-then-write let one of them drop another's edit.
        // False, changing nothing, when edit returns null.
        private bool Edit(Func<EntryList, EntryList?> edit)
        {
            while (true)
            {
                var entries = Volatile.Read(ref _entries);
                if (edit(entries) is not { } next)
                    return false;
                if (ReferenceEquals(Interlocked.CompareExchange(ref _entries, next, entries), entries))
                    return true;
            }
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

        // Bumped on every mutation (rename, track add/remove/reorder). Whether
        // a side changed is decided by content (PlaylistVersion), not by this;
        // it orders two edits that both did - see Touch.
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
            IReadOnlyList<PlaylistEntry>? entries = null)
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
            _entries = entries != null ? new EntryList(UniqueIds(entries)) : new EntryList(new List<Track>(tracks));
            UpdatedAt = updatedAt;
        }

        // An id appearing twice is two rows that could not be told apart, so
        // the later one gets a new id. Entries arrive from other devices.
        private static List<PlaylistEntry> UniqueIds(IReadOnlyList<PlaylistEntry> entries)
        {
            var seen = new HashSet<Guid>();
            var unique = new List<PlaylistEntry>(entries.Count);
            foreach (var entry in entries)
                unique.Add(entry.EntryId != Guid.Empty && seen.Add(entry.EntryId) ? entry : entry with { EntryId = Guid.NewGuid() });
            return unique;
        }

        // Every entry in order, whether this device can play its song or not.
        public IReadOnlyList<PlaylistEntry> Entries => _entries.All;

        // The entries this device can play, slot for slot with Tracks: which
        // entry a row of the list is, and so which of two copies of a song.
        public IReadOnlyList<PlaylistEntry> ShownEntries => _entries.Shown;

        // How many of them it cannot: hidden, and waiting for their song.
        public int UnavailableCount => _entries.UnavailableCount;

        public IEnumerable<Guid> StoredTrackIds() =>
            Entries.Where(e => e.TrackId != null).Select(e => e.TrackId!.Value);

        // A newer copy of this playlist taking over from `previous` (a sync
        // adopting the other side's). A copy sharing entries with it says
        // exactly which rows it holds. One sharing none has no history with
        // this one and could not see these songs either, so its not listing
        // them says nothing, and they stay.
        internal void KeepUnavailableFrom(Playlist previous)
        {
            if (!IsOrdinary || previous.UnavailableCount == 0)
                return;

            Edit(entries =>
            {
                var mine = entries.All.Select(e => e.EntryId).ToHashSet();
                if (previous.Entries.Any(e => mine.Contains(e.EntryId)))
                    return null;

                var present = entries.Tracks.Select(t => t.Id).ToHashSet();
                var merged = entries.All.ToList();
                var at = 0;
                foreach (var entry in previous.Entries)
                {
                    if (entry.Track is { } track)
                    {
                        var followed = merged.FindIndex(e => e.Track?.Id == track.Id);
                        at = followed >= 0 ? followed + 1 : merged.Count;
                        continue;
                    }

                    if (entry.TrackId is { } id && present.Contains(id))
                        continue;

                    merged.Insert(at++, entry);
                }

                return new EntryList(merged);
            });
        }

        public void InsertTrack(int index, Track track)
        {
            var entry = PlaylistEntry.Of(track);
            Edit(entries =>
            {
                var next = entries.All.ToList();
                next.Insert(entries.PlaceOf(index), entry);
                return new EntryList(next);
            });
            Touch();
        }

        public void AppendTrack(Track track) => AppendTracks([track]);

        // Songs added at the end as one edit, however many: one UpdatedAt, one
        // save and one PlaylistsChanged, where one edit per song rewrote every
        // playlist once per song (UF-16). Safe to call off the UI thread, which
        // is where PlaylistManagementViewModel.AddTracks runs a large one.
        public void AppendTracks(IReadOnlyList<Track> tracks)
        {
            if (tracks.Count == 0)
                return;

            var added = tracks.Select(PlaylistEntry.Of).ToList();
            Edit(entries => new EntryList([.. entries.All, .. added]));
            Touch();
        }

        // The entries at these slots (indexes into Tracks), as one edit, or no
        // edit at all when none of them is a slot here. By slot rather than by
        // song because a playlist can hold one song twice, and a song cannot
        // say which of the two was meant. Returns whether anything went.
        public bool RemoveEntriesAt(IReadOnlyCollection<int> slots)
        {
            var removed = Edit(entries =>
            {
                var count = entries.Tracks.Count;
                var doomed = slots.Where(slot => slot >= 0 && slot < count).Select(entries.PlaceOf).ToHashSet();
                return doomed.Count == 0
                    ? null
                    : new EntryList(entries.All.Where((_, place) => !doomed.Contains(place)).ToList());
            });
            if (!removed)
                return false;

            Touch();
            return true;
        }

        // Every entry for any of these tracks, as one edit - or no edit at all,
        // UpdatedAt untouched, when none of them is here. See
        // Library.RemoveTracks.
        public void RemoveTracks(IReadOnlySet<Guid> ids)
        {
            bool Leaves(PlaylistEntry entry) => entry.TrackId is { } id && ids.Contains(id);
            if (!Edit(entries => entries.All.Any(Leaves) ? new EntryList(entries.All.Where(e => !Leaves(e)).ToList()) : null))
                return;

            Touch();
        }

        public void ReplaceAll(List<Track> tracks)
        {
            Edit(entries => entries.HasEntries
                ? new EntryList(KeepingEntryIds(entries.All, tracks))
                : new EntryList(new List<Track>(tracks)));
            Touch();
        }

        // A list worked out again: a song it still holds is still that entry.
        private static List<PlaylistEntry> KeepingEntryIds(IReadOnlyList<PlaylistEntry> old, IReadOnlyList<Track> tracks)
        {
            var waiting = new CopiesBySong<Guid>();
            foreach (var entry in old)
                waiting.Add(entry.TrackId, entry.EntryId);

            var next = new List<PlaylistEntry>(tracks.Count);
            foreach (var track in tracks)
                next.Add(waiting.TryTake(track.Id, out var id) ? new PlaylistEntry(id, track) : PlaylistEntry.Of(track));
            return next;
        }

        // Drag-to-reorder, of one row or a selection (UF-15): the entries at
        // these slots move, in the order they stand, to sit just before the
        // entry now at slot insertBefore - or at the end, for Tracks.Count.
        // One edit, by slot for RemoveEntriesAt's reason. False, changing
        // nothing, not even UpdatedAt, when no slot is here, the place is one
        // of those moving, or the songs this device shows come out in the
        // order they were: a drag that lands back where it started is the most
        // common way a drag ends, and must not become an edit to sync.
        public bool MoveEntries(IReadOnlyCollection<int> slots, int insertBefore)
        {
            var moved = Edit(entries =>
            {
                var count = entries.Tracks.Count;
                if (insertBefore < 0 || insertBefore > count)
                    return null;
                var places = slots.Where(slot => slot >= 0 && slot < count).Distinct().Order().Select(entries.PlaceOf).ToList();
                if (places.Count == 0)
                    return null;

                var all = entries.All;
                var moving = places.ToHashSet();
                var rest = all.Where((_, place) => !moving.Contains(place)).ToList();
                int at;
                if (insertBefore == count)
                {
                    at = rest.Count;
                }
                else
                {
                    var before = entries.PlaceOf(insertBefore);
                    if (moving.Contains(before))
                        return null;
                    at = rest.FindIndex(e => e.EntryId == all[before].EntryId);
                }

                rest.InsertRange(at, places.Select(place => all[place]));
                static IEnumerable<Guid> Shown(IEnumerable<PlaylistEntry> list) =>
                    list.Where(e => e.Track != null).Select(e => e.EntryId);
                return Shown(rest).SequenceEqual(Shown(all)) ? null : new EntryList(rest);
            });
            if (!moved)
                return false;

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
        internal void RebindTracks(
            Func<Track, Track?> current, Func<Guid, Track?> byId, Func<PlaylistSyncTrackDto, Track?> bySong)
        {
            Edit(entries =>
            {
                if (!entries.HasEntries)
                    return new EntryList(entries.Tracks.Select(t => current(t) ?? t).ToList());

                var next = new List<PlaylistEntry>(entries.All.Count);
                foreach (var entry in entries.All)
                {
                    if (entry.Track is { } track)
                    {
                        next.Add(entry with { Track = current(track) ?? track });
                        continue;
                    }

                    var back = (entry.AwayTrackId is { } id ? byId(id) : null)
                        ?? (entry.Song is { } song ? bySong(song) : null);
                    next.Add(back == null || !IsOrdinary ? entry : new PlaylistEntry(entry.EntryId, back));
                }
                return new EntryList(next);
            });
        }

        // This device's copy turned out to hold the same songs as the other
        // side's, under ids of its own: it takes theirs, with no edit to sync.
        // Only if the list is still the one that was compared.
        public bool TakeEntryIdsFrom(IReadOnlyList<PlaylistEntry> compared, IReadOnlyList<PlaylistSyncTrackDto> rows)
        {
            var entries = _entries;
            if (!ReferenceEquals(entries.All, compared) || compared.Count != rows.Count)
                return false;

            var used = new HashSet<Guid>();
            var next = new List<PlaylistEntry>(compared.Count);
            var changed = false;
            for (var i = 0; i < compared.Count; i++)
            {
                if (rows[i].EntryId is { } id && id != Guid.Empty && used.Add(id))
                {
                    changed |= id != compared[i].EntryId;
                    next.Add(compared[i] with { EntryId = id });
                }
                else
                {
                    next.Add(compared[i]);
                }
            }

            return changed
                && ReferenceEquals(Interlocked.CompareExchange(ref _entries, new EntryList(next), entries), entries);
        }

        // Installs the result of evaluating Rules, without touching UpdatedAt.
        // Returns whether the contents actually changed, so a recomputation
        // pass over every smart playlist can persist only the ones that moved.
        //
        // Not Touch()ing, for the reason RebindTracks above is not: a
        // re-evaluation is not a user edit - the songs changed because the
        // library did. Nor is it a change sync carries: PlaylistVersion leaves
        // a smart or Vibe playlist's songs out, so only its rules or sentence
        // can conflict, never what each device worked out from them.
        //
        // Public, unlike RebindTracks, because the recomputation pass that
        // drives it is not Library - see SMART-PLAYLIST-PLAN.md phase 3. A
        // Vibe Playlist's pool is installed the same way and for the same
        // reason (VibePlaylistPools, and a paired device taking its server's).
        public bool Materialize(IReadOnlyList<Track> tracks) => Edit(entries =>
        {
            var current = entries.Tracks;
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
                    return null;
            }

            return entries.HasEntries
                ? new EntryList(KeepingEntryIds(entries.All, tracks))
                : new EntryList(new List<Track>(tracks));
        });

        public Track? GetTrack(int index)
        {
            return Tracks.ElementAtOrDefault(index);
        }
    }
}
