using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Flower.Models
{
    public class Library
    {
        public List<Track> Tracks { get; private set; }
        public List<Playlist> Playlists { get; private set; } = new List<Playlist>();

        public Library(List<Track> tracks)
        {
            Tracks = new List<Track>(tracks);
        }

    }
}
