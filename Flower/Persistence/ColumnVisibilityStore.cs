using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Threading.Tasks;

namespace Flower.Persistence
{
    // Kept for backward-compat with existing config.json reads
    public class ColumnVisibilitySettings
    {
        public bool Title    { get; set; } = true;
        public bool Artist   { get; set; } = true;
        public bool Album    { get; set; } = true;
        public bool Year     { get; set; } = true;
        public bool Genre    { get; set; } = true;
        public bool Duration { get; set; } = true;

        public List<ColumnState>? ColumnStates { get; set; }

        public string? SortColumn    { get; set; }
        public bool    SortAscending { get; set; } = true;

        public bool SortArtistAlbumsByYear { get; set; }
    }

    public class ColumnState
    {
        public string Id        { get; set; } = "";
        public bool   IsVisible { get; set; } = true;
        public double Width     { get; set; } = 100;
        public int    Order     { get; set; } = 0;
    }

    public class ColumnVisibilityStore
    {
        private static string StorePath => Path.Combine(AppDataDirectory.Path, "config.json");

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

        public List<ColumnState>? LoadColumnStates()
            => Load().ColumnStates;

        public (string SortColumn, bool SortAscending)? LoadSortState()
        {
            var settings = Load();
            if (string.IsNullOrEmpty(settings.SortColumn))
                return null;
            return (settings.SortColumn, settings.SortAscending);
        }

        public async Task SaveAsync(ColumnVisibilitySettings settings)
        {
            var path = StorePath;
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var json = JsonSerializer.Serialize(settings, Options);
            await File.WriteAllTextAsync(path, json);
        }

        public async Task SaveColumnStatesAsync(List<ColumnState> states)
        {
            var settings = Load();
            settings.ColumnStates = states;
            await SaveAsync(settings);
        }

        // Synchronous counterpart for the Window.Closing handler (see
        // AppSettingsStore.Save), where the process may exit before the
        // debounced SaveColumnStatesAsync's Task.Delay completes - a resize
        // (or hide/reorder) made shortly before quitting would otherwise never
        // reach disk.
        public void SaveColumnStates(List<ColumnState> states)
        {
            var settings = Load();
            settings.ColumnStates = states;
            var path = StorePath;
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, JsonSerializer.Serialize(settings, Options));
        }

        public async Task SaveSortStateAsync(string sortColumn, bool sortAscending)
        {
            var settings = Load();
            settings.SortColumn    = sortColumn;
            settings.SortAscending = sortAscending;
            await SaveAsync(settings);
        }

        public bool LoadSortArtistAlbumsByYear()
            => Load().SortArtistAlbumsByYear;

        public async Task SaveSortArtistAlbumsByYearAsync(bool value)
        {
            var settings = Load();
            settings.SortArtistAlbumsByYear = value;
            await SaveAsync(settings);
        }
    }
}
