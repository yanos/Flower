using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

using Flower.Models;

namespace Flower.Importer
{
    public class Importer
    {
        private readonly HashSet<string> _validExtensions = [".mp3", ".m4a", ".wav", ".flac", ".alac"];

        public Importer() { }

        public List<Track> Import()
        {
            var tracks = new List<Track>();

            var path = "/Users/yanos/Music"; //"C:\\Users\\ycholette\\Music\\iTunes\\iTunes Media";

            var files = Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories)
                .Where(f => _validExtensions.Contains(Path.GetExtension(f).ToLower()));

            foreach (var file in files)
            {
                try
                {
                    var tagFile = TagLib.File.Create(file);
                    tracks.Add(new Track
                    {
                        Title = tagFile.Tag.Title,
                        Artists = string.Join(", ", tagFile.Tag.Performers),
                        Album = tagFile.Tag.Album,
                        Year = tagFile.Tag.Year.ToString(),
                        Duration = tagFile.Properties?.Duration ?? TimeSpan.Zero,
                        Genre = tagFile.Tag.FirstGenre,
                        Path = file
                    });
                }
                catch { /* skip unreadable files */ }
            }

            return tracks;
        }
    }
}
