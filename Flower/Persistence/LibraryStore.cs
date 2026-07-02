using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Tasks;

using Flower.Models;

namespace Flower.Persistence
{
    public class LibraryStore
    {
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
            catch
            {
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
            catch
            {
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

        private sealed class TimeSpanTicksConverter : JsonConverter<TimeSpan>
        {
            public override TimeSpan Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
                => TimeSpan.FromTicks(reader.GetInt64());

            public override void Write(Utf8JsonWriter writer, TimeSpan value, JsonSerializerOptions options)
                => writer.WriteNumberValue(value.Ticks);
        }
    }
}
