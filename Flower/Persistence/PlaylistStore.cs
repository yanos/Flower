using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;

using Flower.Models;

namespace Flower.Persistence
{
    public class PlaylistStore
    {
        public static string StorePath => Path.Combine(AppDataDirectory.Path, "playlists.json");

        private static readonly JsonSerializerOptions Options = new() { WriteIndented = true };

        // Tracks are stored by file path and resolved against the library on load,
        // so playlists.json never duplicates track metadata.
        private sealed record PlaylistRecord(string Name, List<string> TrackPaths);

        public List<Playlist> Load(IReadOnlyList<Track> libraryTracks)
        {
            var path = StorePath;
            if (!File.Exists(path))
                return new List<Playlist>();

            try
            {
                var json    = File.ReadAllText(path);
                var records = JsonSerializer.Deserialize<List<PlaylistRecord>>(json, Options) ?? new();
                var byPath  = libraryTracks
                    .Where(t => t.Path != null)
                    .GroupBy(t => t.Path!)
                    .ToDictionary(g => g.Key, g => g.First());

                return records
                    .Select(r => new Playlist(
                        r.Name,
                        r.TrackPaths.Where(byPath.ContainsKey).Select(p => byPath[p]).ToList()))
                    .ToList();
            }
            catch
            {
                return new List<Playlist>();
            }
        }

        public async Task SaveAsync(IEnumerable<Playlist> playlists)
        {
            var path = StorePath;
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);

            var records = playlists
                .Select(p => new PlaylistRecord(
                    p.Name,
                    p.Tracks.Where(t => t.Path != null).Select(t => t.Path!).ToList()))
                .ToList();

            var json = JsonSerializer.Serialize(records, Options);
            await File.WriteAllTextAsync(path, json);
        }
    }
}
