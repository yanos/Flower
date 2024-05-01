using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Flower.Models
{
    public class Library
    {
        public IReadOnlyCollection<Track> Tracks { get; private set; }
        public IReadOnlyCollection<Playlist> Playlists { get; private set; }

        public Library(ICollection<Track> tracks)
        {
            Tracks = new List<Track>(tracks);
        }

    }
}
