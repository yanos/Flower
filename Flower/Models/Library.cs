using System;
using System.Collections.Generic;
using System.Linq;

namespace Flower.Models
{
    public class Library
    {
        public List<Track> Tracks { get; private set; }
        public List<Playlist> Playlists { get; private set; } = new List<Playlist>();

        public event EventHandler? TracksUpdated;

        // Fired when PlaylistSyncService/SyncHttpServer replace the playlist set as
        // a result of syncing with another device - see ReplacePlaylists. Local UI
        // actions (create/rename/add-track) manage sidebar state inline instead of
        // relying on this event, so this only needs to cover the sync path.
        public event EventHandler? PlaylistsUpdated;

        public Library(List<Track> tracks)
        {
            Tracks = new List<Track>(tracks);
        }

        // A rescan (see Importer) produces brand-new Track instances read straight
        // from file tags, each defaulting DateAdded to "now" - so without this,
        // every track would look freshly added after every launch. Carry the
        // original DateAdded forward for any track already known by Path.
        public void UpdateTracks(List<Track> tracks)
        {
            var previousDateAddedByPath = Tracks
                .Where(t => t.Path != null)
                .GroupBy(t => t.Path!, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(g => g.Key, g => g.First().DateAdded, StringComparer.OrdinalIgnoreCase);

            foreach (var track in tracks)
            {
                if (track.Path != null && previousDateAddedByPath.TryGetValue(track.Path, out var dateAdded))
                    track.DateAdded = dateAdded;
            }

            Tracks = new List<Track>(tracks);
            TracksUpdated?.Invoke(this, EventArgs.Empty);
        }

        public void AddPlaylist(Playlist playlist)
        {
            Playlists.Add(playlist);
        }

        public void RemovePlaylist(Playlist playlist)
        {
            Playlists.Remove(playlist);
        }

        // Atomically swaps in a merged playlist set from a sync session and notifies
        // listeners - see PlaylistsUpdated.
        public void ReplacePlaylists(List<Playlist> playlists)
        {
            Playlists = new List<Playlist>(playlists);
            PlaylistsUpdated?.Invoke(this, EventArgs.Empty);
        }
    }
}
