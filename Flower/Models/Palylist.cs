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
                UpdatedAt = DateTimeOffset.UtcNow;
            }
        }

        public List<Track> Tracks { get; }

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
            Tracks = tracks;
            UpdatedAt = updatedAt;
        }

        public void InsertTrack(int index, Track track)
        {
            Tracks.Insert(index, track);
            UpdatedAt = DateTimeOffset.UtcNow;
        }

        public void AppendTrack(Track track)
        {
            Tracks.Add(track);
            UpdatedAt = DateTimeOffset.UtcNow;
        }

        public void RemoveTrack(Track track)
        {
            Tracks.Remove(track);
            UpdatedAt = DateTimeOffset.UtcNow;
        }

        public void ReplaceAll(List<Track> tracks)
        {
            Tracks.Clear();
            Tracks.AddRange(tracks);
            UpdatedAt = DateTimeOffset.UtcNow;
        }

        public Track? GetTrack(int index)
        {
            return Tracks.ElementAtOrDefault(index);
        }

        public Track? GetPreviousTrack(Track currentTrack)
        {
            int index = Tracks.IndexOf(currentTrack);
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
            int index = Tracks.IndexOf(currentTrack);
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
