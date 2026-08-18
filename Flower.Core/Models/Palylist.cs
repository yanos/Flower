using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

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
        private List<Track> _tracks;

        // Read-only so that every mutation has to go through the methods below,
        // which are what bump UpdatedAt and raise Changed. It used to be a bare
        // List<Track>, and MainViewModel.ReorderPlaylistTrack duly reached in
        // and did its own Remove()+Insert() - so a drag-reorder changed the
        // playlist without bumping UpdatedAt at all, and PlaylistSyncPlanner
        // (which decides "did this side change?" purely from UpdatedAt against
        // a per-peer baseline) never saw the reorder and never propagated it.
        public IReadOnlyList<Track> Tracks => _tracks;

        // Raised after any mutation that bumps UpdatedAt. Library subscribes to
        // every playlist it holds and relays this as Library.PlaylistsChanged,
        // which is what actually drives persistence - see Library.PlaylistsChanged
        // for why that has to happen here rather than at each UI call site.
        public event EventHandler? Changed;

        private void Touch()
        {
            UpdatedAt = DateTimeOffset.UtcNow;
            Changed?.Invoke(this, EventArgs.Empty);
        }

        // Bumped on every mutation (rename, track add/remove/reorder). Sync uses
        // this against a per-peer last-synced baseline to tell which side(s)
        // changed since they last agreed - see PlaylistSyncPlanner.
        public DateTimeOffset UpdatedAt { get; private set; }

        public Playlist(string name, List<Track> tracks) : this(Guid.NewGuid(), name, tracks, DateTimeOffset.UtcNow)
        {
        }

        public Playlist(Guid id, string name, List<Track> tracks, DateTimeOffset updatedAt)
        {
            Id = id;
            _name = name;
            // Defensive copy, matching Library's own constructor - callers can pass a
            // list they keep their own reference to (App.axaml.cs constructs
            // MainPlaylist directly from library.Tracks). Without this, ReplaceAll's
            // Clear()+AddRange() mutates that same underlying list in place, and
            // since ReplaceAll always runs immediately before Library.UpdateTracks
            // (both here and in RebuildDatabaseAsync), UpdateTracks would read its
            // own "previous" snapshot from a list that had *already* been overwritten
            // with the fresh (PlayCount/DateAdded/ImportedPlayCount-defaulted) data -
            // silently discarding whatever was actually there, every single rescan.
            _tracks = new List<Track>(tracks);
            UpdatedAt = updatedAt;
        }

        public void InsertTrack(int index, Track track)
        {
            var next = new List<Track>(_tracks);
            next.Insert(index, track);
            _tracks = next;
            Touch();
        }

        public void AppendTrack(Track track)
        {
            _tracks = new List<Track>(_tracks) { track };
            Touch();
        }

        public void RemoveTrack(Track track)
        {
            var next = new List<Track>(_tracks);
            if (!next.Remove(track))
                return;

            _tracks = next;
            Touch();
        }

        public void ReplaceAll(List<Track> tracks)
        {
            _tracks = new List<Track>(tracks);
            Touch();
        }

        // Drag-to-reorder: move an entry to sit immediately before insertBefore,
        // or to the end when that is null. Returns false - changing nothing,
        // not even UpdatedAt - when the drag would not actually reorder
        // anything: the dragged track is not in this playlist, it was dropped
        // onto itself, or it was dropped where it already sits (onto the entry
        // that already follows it, or at the end when it is already last).
        //
        // That last group matters beyond tidiness. UpdatedAt is the entire
        // basis on which PlaylistSyncPlanner decides "did this side change?"
        // against its per-peer baseline, so bumping it here would manufacture a
        // sync-visible edit out of a drag that moved nothing - and a drag that
        // lands back where it started is the single most common way a
        // drag-reorder ends. Same reasoning as RemoveTrack's own no-op guard;
        // see docs/ARCHITECTURE-REVIEW.md 2.4.
        public bool MoveTrack(Track dragged, Track? insertBefore)
        {
            var originalIndex = _tracks.IndexOf(dragged);
            if (originalIndex < 0)
                return false;

            // Dropped onto itself. Without this the removal below happens
            // first, IndexOf then fails to find insertBefore in the shortened
            // list, and the track silently lands at the end instead.
            if (insertBefore == dragged)
                return false;

            var next = new List<Track>(_tracks);
            next.RemoveAt(originalIndex);

            var index = insertBefore != null ? next.IndexOf(insertBefore) : -1;
            var targetIndex = index < 0 ? next.Count : index;
            // Removing at originalIndex and re-inserting at the same index
            // reproduces the list exactly.
            if (targetIndex == originalIndex)
                return false;

            next.Insert(targetIndex, dragged);
            _tracks = next;
            Touch();
            return true;
        }

        // Swaps each entry for whichever Track instance now represents it,
        // without touching UpdatedAt - see Library.RebindPlaylistTracks, which
        // is the only caller and explains why. Not a mutation in the sense the
        // methods above are: same songs, same order, same playlist, just the
        // objects a rescan replaced underneath it.
        internal void RebindTracks(IReadOnlyDictionary<Guid, Track> byId)
        {
            var next = new List<Track>(_tracks);
            for (var i = 0; i < next.Count; i++)
            {
                if (byId.TryGetValue(next[i].Id, out var current))
                    next[i] = current;
            }
            _tracks = next;
        }

        public Track? GetTrack(int index)
        {
            return Tracks.ElementAtOrDefault(index);
        }

        public Track? GetPreviousTrack(Track currentTrack)
        {
            int index = _tracks.IndexOf(currentTrack);
            if (index == -1)
            {
                return Tracks.FirstOrDefault();
            }
            else
            {
                return Tracks.ElementAtOrDefault(index - 1) ?? Tracks.FirstOrDefault();
            }
        }

        public Track? GetNextTrack(Track currentTrack)
        {
            int index = _tracks.IndexOf(currentTrack);
            if (index == -1)
            {
                return Tracks.FirstOrDefault();
            }
            else
            {
                return Tracks.ElementAtOrDefault(index + 1) ?? Tracks.FirstOrDefault();
            }
        }
    }
}
