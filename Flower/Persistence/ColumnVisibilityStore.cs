using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Threading.Tasks;

namespace Flower.Persistence
{
    public class ColumnVisibilitySettings
    {
        public bool Title { get; set; } = true;
        public bool Artist { get; set; } = true;
        public bool Album { get; set; } = true;
        public bool Year { get; set; } = true;
        public bool Genre { get; set; } = true;
        public bool Duration { get; set; } = true;
    }

    public class ColumnVisibilityStore
    {
        private static string StorePath
        {
            get
            {
                string dir = RuntimeInformation.IsOSPlatform(OSPlatform.OSX)
                    ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                        "Library", "Application Support", "Flower")
                    : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                        "Flower");
                return Path.Combine(dir, "config.json");
            }
        }

        private static readonly JsonSerializerOptions Options = new() { WriteIndented = true };

        public ColumnVisibilitySettings Load()
        {
            var path = StorePath;
            if (!File.Exists(path))
                return new ColumnVisibilitySettings();
            try
            {
                var json = File.ReadAllText(path);
                return JsonSerializer.Deserialize<ColumnVisibilitySettings>(json, Options)
                    ?? new ColumnVisibilitySettings();
            }
            catch
            {
                return new ColumnVisibilitySettings();
            }
        }

        public async Task SaveAsync(ColumnVisibilitySettings settings)
        {
            var path = StorePath;
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var json = JsonSerializer.Serialize(settings, Options);
            await File.WriteAllTextAsync(path, json);
        }
    }
}
