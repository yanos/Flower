using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Flower.Models
{
    public class MainPlaylist : Playlist
    {
        public MainPlaylist(List<Track> tracks) : base("Main", tracks)
        {
        }
    }
}
