using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;

using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

using Serilog.Events;

using Flower.Manager;

namespace Flower.Persistence
{
    // Settings > Appearance - see Flower.Services.AppTheme for how this
    // translates into Avalonia's own ThemeVariant.
    [JsonConverter(typeof(JsonStringEnumConverter<AppThemePreference>))]
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

        // Master switch for every way Flower reaches into a local
        // iTunes/Music.app installation: auto-registering Music.app's own
        // configured media folder as a library path (see Load below) and the
        // two per-track imports underneath. On by default - on a Mac with a
        // Music.app library, having Flower pick that library up on its own is
        // what most people want; turning this off makes Flower ignore
        // Music.app entirely and leaves the library purely whatever folders
        // were added by hand. The two flags below stay independently
        // meaningful (and keep their own persisted values) but are inert
        // while this is false.
        public bool IntegrateWithITunes { get; set; } = true;

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

        // Set once a bulk sync with PairedServerFingerprint has actually
        // succeeded - i.e. the server-side approval popup (SyncHttpServer.
        // AuthorizeAsync/PeerApprovalRequested) has been answered "yes", not
        // merely that this Client has asked to pair. False the whole time
        // PairedServerFingerprint is set but every sync attempt is still
        // getting a 403 - see MainViewModel.IsAwaitingServerApproval, shown
        // as the device-detail header's "Waiting for server..." spinner.
        // Reset to false on every new PairWithServer call (a fresh pairing
        // request starts unconfirmed again).
        public bool PairedServerTrustConfirmed { get; set; }

        // Whether this Client pushes its own recent log lines to its paired
        // Server at the end of each library sync (LibrarySyncService.
        // PushLogSnapshotAsync, read back by the Log window's remote view).
        //
        // Off by default, and deliberately opt-in rather than merely
        // role-gated: the P2P transport is plaintext HTTP by design (TLS is
        // permanently deferred there - see SYNC-PLAN.md), and a log snapshot
        // is the highest-value payload that path carries. It contains
        // exception text and absolute filesystem paths, i.e. usernames and
        // library layout, which nothing else in the sync protocol exposes.
        // Sending that in the clear on someone else's Wi-Fi has to be a
        // choice the user made, not a default.
        public bool ShareLogsWithPairedServer { get; set; } = false;

        // Log window preferences (View > Log...), remembered between
        // launches the same way IsRepeatEnabled/IsShuffleEnabled are - see
        // LogViewModel.
        public double        LogFontSize        { get; set; } = 12;
        public LogEventLevel LogMinimumLevel    { get; set; } = LogEventLevel.Verbose;
        public bool          LogWordWrapEnabled { get; set; } = false;

        // EQ window (View > Equalizer...) preferences, remembered between
        // launches the same way IsRepeatEnabled/IsShuffleEnabled are - see
        // EqualizerViewModel. Null until the user opens the Equalizer window
        // at least once (distinct from Enabled=false, which is an explicit
        // bypass) - eagerly re-applied at startup in App.axaml.cs, not only
        // when the window is opened.
        public EqualizerSettings? EqualizerSettings { get; set; }
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

        public AppSettings Load()
        {
            var stored = LoadFromDisk();
            var settings = stored ?? new AppSettings();
            var changed = false;

            // Auto-register Apple Music's configured media folder, if found and not
            // already present, so it shows up in Settings without the user having to
            // browse for a folder they've already pointed Music.app at. Skipped
            // entirely when the iTunes integration is off - that switch is also what
            // makes removing this folder in Settings stick, since otherwise the next
            // launch just puts it back.
            if (settings.IntegrateWithITunes &&
                Importer.Importer.TryResolveAppleMusicFolder(_logger) is string appleMusicFolder &&
                !settings.LibraryPaths.Any(p => string.Equals(p, appleMusicFolder, StringComparison.OrdinalIgnoreCase)))
            {
                settings.LibraryPaths.Add(appleMusicFolder);
                changed = true;
            }

            // First run only (no settings file on disk yet), and only if nothing
            // above already gave this device a folder: start from the platform's
            // own music folder so a fresh install has something to scan. Seeded
            // into the persisted list rather than applied as a scan-time default,
            // so that it is visible in Settings and - unlike the invisible
            // fallback Importer used to have - stays removed once removed. Mobile
            // is excluded: Android scans via MediaStore rather than a path, and
            // iOS's own Documents directory is handled inside Importer, since its
            // absolute path can change across a reinstall and so must not be
            // persisted here.
            if (stored is null && settings.LibraryPaths.Count == 0 &&
                !OperatingSystem.IsIOS() && !OperatingSystem.IsAndroid() &&
                Environment.GetFolderPath(Environment.SpecialFolder.MyMusic) is { Length: > 0 } musicFolder &&
                Directory.Exists(musicFolder))
            {
                _logger.LogInformation("First run - seeding library folders with {MusicFolder}", musicFolder);
                settings.LibraryPaths.Add(musicFolder);
                changed = true;
            }

            if (changed)
                Save(settings);

            return settings;
        }

        private AppSettings? LoadFromDisk() =>
            AtomicJsonFile.Read(StorePath, FlowerJsonContext.Default.AppSettings, _logger);

        // ColumnManager's debounced save fires on every column resize/reorder/
        // hide, so overlapping writes to settings.json are routine rather than
        // exceptional - the same reason LibraryStore needed a lock, applied here
        // too rather than left as the one unprotected store.
        private readonly SemaphoreSlim _writeLock = new(1, 1);

        public async Task SaveAsync(AppSettings settings)
        {
            await _writeLock.WaitAsync();
            try
            {
                await AtomicJsonFile.WriteAsync(StorePath, settings, FlowerJsonContext.Default.AppSettings);
            }
            finally
            {
                _writeLock.Release();
            }
        }

        // Synchronous counterpart for the Window.Closing handler, where the
        // process may exit before an async save completes.
        public void Save(AppSettings settings)
        {
            _writeLock.Wait();
            try
            {
                AtomicJsonFile.Write(StorePath, settings, FlowerJsonContext.Default.AppSettings);
            }
            finally
            {
                _writeLock.Release();
            }
        }
    }
}
