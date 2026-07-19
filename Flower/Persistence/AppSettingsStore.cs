using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Tasks;

using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Flower.Persistence
{
    // Settings > Appearance - see Flower.Services.AppTheme for how this
    // translates into Avalonia's own ThemeVariant.
    public enum AppThemePreference
    {
        System,
        Light,
        Dark,
    }

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

        // Track list column state (width/visibility/order - see ColumnManager)
        // and sort state (see MainViewModel).
        public List<ColumnState>? ColumnStates { get; set; }
        public string? SortColumn    { get; set; }
        public bool    SortAscending { get; set; } = true;

        // When sorting by Artist, order each artist's albums by year instead of
        // however they happened to appear - see MainViewModel.SortArtistAlbumsByYear.
        public bool SortArtistAlbumsByYear { get; set; }

        // Whether to import per-track play counts from iTunes/Music.app's
        // optional library XML export on every launch - see
        // ITunesPlayCountImporter and Track.ImportedPlayCount. On by default;
        // it's a harmless no-op when no such export exists on disk.
        public bool SyncPlayCountFromITunes { get; set; } = true;

        // Same export, but for Track.DateAdded instead of play counts - see
        // ITunesDateAddedImporter. Off by default, unlike SyncPlayCountFromITunes:
        // this can visibly reorder Recently Added (the older of the two dates
        // always wins - see ITunesDateAddedImporter's own doc comment), so it is
        // opt-in rather than silently changing sort order for everyone the first
        // time they update.
        public bool SyncDateAddedFromITunes { get; set; } = false;

        // Follows the OS light/dark setting by default; Light/Dark force that
        // variant regardless of the OS - see Settings' Appearance picker and
        // Flower.Services.AppTheme.
        public AppThemePreference ThemePreference { get; set; } = AppThemePreference.System;

        // Which sidebar view (and, for a Playlist, which one - by name, since
        // Playlist objects themselves aren't stable across a relaunch) and how
        // far scrolled into it the user was, saved on close (MainWindow.Closing,
        // same synchronous-Save timing as the window geometry above) and
        // restored on the next launch - see MainViewModel.BuildSidebarItems and
        // MainView.axaml.cs's SaveCurrentViewState/RestoreInitialViewState.
        // LastSidebarKind is SidebarItemKind's own name ("Songs", "Albums",
        // "Playlist", etc.), not a raw int, so a future enum reorder can't
        // silently restore the wrong view.
        public string? LastSidebarKind    { get; set; }
        public string? LastPlaylistName   { get; set; }
        public double  LastScrollOffsetY  { get; set; }

        // Whether this device accepts incoming bulk-sync from Client devices
        // (Server) or initiates bulk-sync toward exactly one chosen Server
        // (Client, the default) - see Settings' General tab and
        // Flower.Services.SyncRolePolicy. This only governs the
        // LibrarySyncService/PlaylistSyncService bulk merge - browsing/
        // streaming another device's catalog (PeerOpenSubsonicClientFactory)
        // is unrestricted by role.
        public bool IsServer { get; set; } = false;

        // The one Server this Client currently bulk-syncs with, picked
        // manually from MainViewModel.AvailableServers - null if not yet
        // paired. PairedServerAlias is a display-only cache so Settings can
        // show the paired server's name even while it is not currently
        // reachable, without a live lookup.
        public string? PairedServerFingerprint { get; set; }
        public string? PairedServerAlias       { get; set; }
    }

    public class ColumnState
    {
        public string Id        { get; set; } = "";
        public bool   IsVisible { get; set; } = true;
        public double Width     { get; set; } = 100;
        public int    Order     { get; set; } = 0;
    }

    public class AppSettingsStore
    {
        private readonly ILogger<AppSettingsStore> _logger;

        // Convenience overload for the many call sites (mostly tests) that don't
        // care about log output - production code always goes through the other
        // constructor instead (see App.axaml.cs), which gets a real, properly
        // DI-configured ILogger<AppSettingsStore>.
        public AppSettingsStore() : this(NullLogger<AppSettingsStore>.Instance) { }

        public AppSettingsStore(ILogger<AppSettingsStore> logger)
        {
            _logger = logger;
        }

        public static string StorePath => Path.Combine(AppDataDirectory.Path, "settings.json");

        // Converters: ThemePreference round-trips as "System"/"Light"/"Dark" in
        // settings.json rather than an opaque integer.
        private static readonly JsonSerializerOptions Options = new()
        {
            WriteIndented = true,
            Converters = { new JsonStringEnumConverter() },
        };

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

        private AppSettings LoadFromDisk()
        {
            var path = StorePath;
            if (!File.Exists(path))
                return new AppSettings();

            try
            {
                var json = File.ReadAllText(path);
                return JsonSerializer.Deserialize<AppSettings>(json, Options) ?? new AppSettings();
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to load settings from {Path}; using defaults", path);
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
