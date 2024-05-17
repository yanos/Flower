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

        public List<Track> Import()
        {
            var tracks = new List<Track>();

            var path = "C:\\Users\\ycholette\\Music\\iTunes\\iTunes Media";

            var files = Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories)
                .Where(f => _validExtensions.Contains(Path.GetExtension(f).ToLower()));

            foreach (var file in files)
            {
                var tagFile = TagLib.File.Create(file);
                var track = new Track
                {
                    Title = tagFile.Tag.Title,
                    Artists = string.Join(", ", tagFile.Tag.Performers),
                    Album = tagFile.Tag.Album,
                    Year = tagFile.Tag.Year.ToString(),
                    Duration = tagFile.Properties.Duration,
                    Genre = tagFile.Tag.FirstGenre,
                    Path = file
                };

                tracks.Add(track);
            }

            return tracks;
        }
    }
}
