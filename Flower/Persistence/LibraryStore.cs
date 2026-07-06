using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Tasks;

using Microsoft.Extensions.Logging;

using Flower.Logging;
using Flower.Models;

namespace Flower.Persistence
{
    public class LibraryStore
    {
        private static readonly ILogger Logger = AppLogging.CreateLogger<LibraryStore>();

        public static string StorePath => Path.Combine(AppDataDirectory.Path, "library.json");

        private static readonly JsonSerializerOptions Options = new()
        {
            WriteIndented = true,
            Converters = { new TimeSpanTicksConverter() }
        };

        public List<Track> Load()
        {
            var path = StorePath;
            if (!File.Exists(path))
                return new List<Track>();

            try
            {
                var json = File.ReadAllText(path);
                return JsonSerializer.Deserialize<List<Track>>(json, Options) ?? new List<Track>();
            }
            catch (Exception ex)
            {
                // Corrupt/unreadable library.json would otherwise silently look
                // like "empty library" with no clue why - this is exactly the
                // kind of thing you need in a bug report.
                Logger.LogWarning(ex, "Failed to load library from {Path}; starting with an empty library", path);
                return new List<Track>();
            }
        }

        public async Task<List<Track>> LoadAsync()
        {
            var path = StorePath;
            if (!File.Exists(path))
                return new List<Track>();

            try
            {
                var json = await File.ReadAllTextAsync(path);
                return JsonSerializer.Deserialize<List<Track>>(json, Options) ?? new List<Track>();
            }
            catch (Exception ex)
            {
                Logger.LogWarning(ex, "Failed to load library from {Path}; starting with an empty library", path);
                return new List<Track>();
            }
        }

        public async Task SaveAsync(IEnumerable<Track> tracks)
        {
            var path = StorePath;
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var json = JsonSerializer.Serialize(tracks, Options);
            await File.WriteAllTextAsync(path, json);
        }

        // Synchronous counterpart for the Window.Closing handler, where the
        // process may exit before an async save completes - see AppSettingsStore.Save
        // and ColumnManager.Flush for the same pattern. Without this, quitting
        // shortly after a track naturally ends (PlaylistControlViewModel.EndReached
        // increments PlayCount and kicks off a fire-and-forget SaveAsync) can exit
        // before that write lands, so the increment is silently lost on next launch.
        public void Save(IEnumerable<Track> tracks)
        {
            var path = StorePath;
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, JsonSerializer.Serialize(tracks, Options));
        }

        private sealed class TimeSpanTicksConverter : JsonConverter<TimeSpan>
        {
            public override TimeSpan Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
                => TimeSpan.FromTicks(reader.GetInt64());

            public override void Write(Utf8JsonWriter writer, TimeSpan value, JsonSerializerOptions options)
                => writer.WriteNumberValue(value.Ticks);
        }
    }
}
