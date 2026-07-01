using System;
using System.Collections.Generic;

namespace Flower.Models
{
    public class Library
    {
        public List<Track> Tracks { get; private set; }
        public List<Playlist> Playlists { get; private set; } = new List<Playlist>();

        public event EventHandler? TracksUpdated;

        public Library(List<Track> tracks)
        {
            Tracks = new List<Track>(tracks);
        }

        public void UpdateTracks(List<Track> tracks)
        {
            Tracks = new List<Track>(tracks);
            TracksUpdated?.Invoke(this, EventArgs.Empty);
        }

        public void AddPlaylist(Playlist playlist)
        {
            Playlists.Add(playlist);
        }
    }
}
