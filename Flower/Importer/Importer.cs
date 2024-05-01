using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

using Flower.Models;

namespace Flower.Importer
{
    internal class Importer
    {
        private readonly HashSet<string> _validExtensions = [".mp3", ".m4a", ".wav", ".flac", ".alac"];

        public Importer() { }

        public ICollection<Track> Import()
        {
            var tracks = new List<Track>();

            var path = "C:\\Users\\ycholette\\Music\\iTunes\\iTunes Media";

            var files = Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories)
                .Where(f => _validExtensions.Contains(Path.GetExtension(f).ToLower()));

            foreach (var file in files)
            {
                var track = new Track
                {
                    Name = Path.GetFileNameWithoutExtension(file),
                    Path = file
                };

                tracks.Add(track);
            }

            return tracks;
        }
    }
}
