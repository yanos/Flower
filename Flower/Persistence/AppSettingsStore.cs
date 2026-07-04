using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;

namespace Flower.Persistence
{
    public class AppSettings
    {
        public List<string> LibraryPaths { get; set; } = new();

        // Main window geometry, saved on close and restored on the next
        // launch. Null until the window has been closed at least once (first
        // run falls back to Avalonia's own default size/placement).
        public double? WindowWidth       { get; set; }
        public double? WindowHeight      { get; set; }
        public double? WindowX           { get; set; }
        public double? WindowY           { get; set; }
        public bool    WindowIsMaximized { get; set; }

        // Repeat/shuffle toggles in the currently-playing control, remembered between launches.
        public bool IsRepeatEnabled  { get; set; }
        public bool IsShuffleEnabled { get; set; }
    }

    public class AppSettingsStore
    {
        public static string StorePath => Path.Combine(AppDataDirectory.Path, "settings.json");

        private static readonly JsonSerializerOptions Options = new() { WriteIndented = true };

        public AppSettings Load()
        {
            var settings = LoadFromDisk();

            // Auto-register Apple Music's configured media folder, if found and not
            // already present, so it shows up in Settings without the user having to
            // browse for a folder they've already pointed Music.app at.
            if (Importer.Importer.TryResolveAppleMusicFolder() is string appleMusicFolder &&
                !settings.LibraryPaths.Any(p => string.Equals(p, appleMusicFolder, StringComparison.OrdinalIgnoreCase)))
            {
                settings.LibraryPaths.Add(appleMusicFolder);
                var path = StorePath;
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                File.WriteAllText(path, JsonSerializer.Serialize(settings, Options));
            }

            return settings;
        }

        private static AppSettings LoadFromDisk()
        {
            var path = StorePath;
            if (!File.Exists(path))
                return new AppSettings();

            try
            {
                var json = File.ReadAllText(path);
                return JsonSerializer.Deserialize<AppSettings>(json, Options) ?? new AppSettings();
            }
            catch
            {
                return new AppSettings();
            }
        }

        public async Task SaveAsync(AppSettings settings)
        {
            var path = StorePath;
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var json = JsonSerializer.Serialize(settings, Options);
            await File.WriteAllTextAsync(path, json);
        }

        // Synchronous counterpart for the Window.Closing handler, where the
        // process may exit before an async save completes.
        public void Save(AppSettings settings)
        {
            var path = StorePath;
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, JsonSerializer.Serialize(settings, Options));
        }
    }
}
