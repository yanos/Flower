using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Flower.Models
{
    public class Playlist
    {
        public string Name { get; }
        public List<Track> Tracks { get; }

        public Playlist(string name, List<Track> tracks)
        {
            Name = name;
            Tracks = tracks;
        }

        public void InsertTrack(int index, Track track)
        {
            Tracks.Insert(index, track);
        }

        public void AppendTrack(Track track)
        {
            Tracks.Add(track);
        }

        public void RemoveTrack(Track track)
        {
            Tracks.Remove(track);
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
