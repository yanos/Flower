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

using Flower.Audio;

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

    // Everything the app persists to settings.json. The library folders and the
    // iTunes switches come from MusicLibrarySettings, which Flower.Server's own
    // options type derives from as well - see there for why only that much is
    // shared and the rest of this stays app-only.
    public class AppSettings : MusicLibrarySettings
    {
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

        // Whether the track list keeps its album-art well - see
        // ColumnManager.ShowAlbumArt, which is what reads and writes this, and
        // ColumnSelectorWindow, where it sits alongside the columns proper.
        // On by default: the art is most of what the list looks like.
        public bool ShowAlbumArtColumn { get; set; } = true;

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

        // The one server this device currently syncs with, picked manually
        // from MainViewModel.AvailableServers - null if not yet paired. PairedServerAlias is a display-only cache so Settings can
        // show the paired server's name even while it is not currently
        // reachable, without a live lookup.
        public string? PairedServerFingerprint { get; set; }
        public string? PairedServerAlias       { get; set; }

        // Set once a bulk sync with PairedServerFingerprint has actually
        // succeeded - i.e. the server really did accept the redeemed pairing
        // code, not merely that this device pointed itself at one. False the whole time
        // PairedServerFingerprint is set but every sync attempt is still
        // getting a 403 - see MainViewModel.IsAwaitingServerApproval, shown
        // as the device-detail header's "Waiting for server..." spinner.
        // Reset to false on every new PairWithServer call (a fresh pairing
        // request starts unconfirmed again).
        public bool PairedServerTrustConfirmed { get; set; }

        // When a bulk sync with PairedServerFingerprint last actually succeeded.
        // Persisted rather than kept in memory because the question it answers -
        // "is what I'm looking at current?" - is asked right after a relaunch as
        // often as during a session. Cleared on unpair, along with the pointer
        // itself. Shown under the server's name in Settings > Devices.
        public DateTimeOffset? PairedServerLastSyncedAt { get; set; }

        // Every address the paired Server has told us it can be reached on,
        // from the addresses field of its /info handshake (see
        // SyncInfoResponseDto and LocalAddresses). Refreshed - replaced, not
        // merged - on each successful handshake, so an address the server has
        // stopped reporting stops being probed.
        //
        // This is what makes a paired server survive leaving the house. Before
        // it, reachability *was* mDNS discovery, and mDNS is link-local: off
        // the home network the paired server simply vanished, with no record of
        // any address to fall back on. See PairedServerReachability and
        // docs/REMOTE-ACCESS-PLAN.md.
        public List<string> PairedServerAddresses { get; set; } = [];

        // Addresses the user typed in themselves, for a server they have never
        // shared a network with and therefore could never discover. Kept apart
        // from PairedServerAddresses because these are the user's to remove and
        // must survive the refresh above, which otherwise overwrites whatever
        // the server most recently reported.
        public List<string> ManualServerAddresses { get; set; } = [];

        // Whether this Client pushes its own recent log lines to its paired
        // Server at the end of each library sync (LibrarySyncService.
        // PushLogSnapshotAsync). The server merges overlapping snapshots into
        // the rolling seven-day history shown in its Logs tab.
        //
        // On by default: the person who runs the server is the person who ends
        // up diagnosing a listener's phone, and a listener cannot be talked
        // through finding a log file over the phone. A snapshot that is only
        // there when somebody thought to turn it on ahead of time is a
        // snapshot that is never there when it is wanted.
        //
        // Still a switch, because of what the payload is: exception text and
        // absolute filesystem paths, i.e. usernames and library layout, which
        // nothing else in the sync protocol exposes. Anyone who would rather
        // not hand that over, even to their own server, can say so.
        public bool ShareLogsWithPairedServer { get; set; } = true;

        // Log viewer preferences, remembered between launches the same way
        // IsRepeatEnabled/IsShuffleEnabled are - see LogViewerViewModel. Shared
        // by both places a log is read: the app's own Log window (View > Log...)
        // and the Logs tab of a server's settings.
        public double        LogFontSize        { get; set; } = 12;
        public LogEventLevel LogMinimumLevel    { get; set; } = LogEventLevel.Verbose;
        public bool          LogWordWrapEnabled { get; set; } = false;

        // Where the settings screen was left, so coming back to it comes back
        // to the same place instead of to General every time - see
        // SettingsViewModel.RememberedTab. Two of them rather than one: a
        // device's own settings and a server's are different screens with
        // different tabs, and a single shared key would land somebody on
        // "Network" in a screen that has no network to configure. Empty until
        // they have been somewhere other than the first tab.
        public string SettingsTab       { get; set; } = "";
        public string ServerSettingsTab { get; set; } = "";

        // Whose log that server's Logs tab was showing: the fingerprint of a
        // device on its roster, or empty for the server's own log. A remembered
        // device that is not on the roster of whatever server is being looked
        // at now (it was forgotten, or this is a different server) falls back
        // to the server's own.
        public string ServerSettingsLogSource { get; set; } = "";

        // The floor for what reaches the log at all, as opposed to LogMinimumLevel
        // above, which only filters what an already-written entry shows as. Debug
        // by default: the per-tick lines (discovery polls every 5s, LibVLC
        // callback tracing) sit at Verbose so they are not written unless someone
        // is actually chasing a bug. Raising this to Verbose is the "turn the
        // noise on" switch; read at startup by App.axaml.cs, so it takes effect
        // on the next launch rather than immediately.
        // Written as a name rather than a number, unlike LogMinimumLevel above:
        // this is the one log setting somebody edits by hand, because turning it
        // up is what you do *before* the run you want logged, and "Verbose"
        // survives being read back by a human where 0 does not.
        [JsonConverter(typeof(JsonStringEnumConverter<LogEventLevel>))]
        public LogEventLevel LogFileMinimumLevel { get; set; } = LogEventLevel.Debug;

        // EQ window (View > Equalizer...) preferences, remembered between
        // launches the same way IsRepeatEnabled/IsShuffleEnabled are - see
        // EqualizerViewModel. Null until the user opens the Equalizer window
        // at least once (distinct from Enabled=false, which is an explicit
        // bypass) - eagerly re-applied at startup in App.axaml.cs, not only
        // when the window is opened.
        public EqualizerSettings? EqualizerSettings { get; set; }

        // Render-path latency/declick tuning - see AudioTimingSettings. Not
        // null-by-default like EqualizerSettings above: there is no "the user
        // has never touched this" state to distinguish, only defaults that can
        // be overridden by hand-editing settings.json. Applied at startup in
        // App.axaml.cs the same way the equalizer is.
        public AudioTimingSettings AudioTiming { get; set; } = new();

        // Which decoder turns a file into PCM - see DecoderElection, and
        // docs/AUDIOPHILE-PLAN.md for why there are two.
        //
        // Hand-edited like AudioTiming above, and for a stronger reason than
        // "no UI has been built yet": a picker in Settings would offer every
        // listener a choice that only resolves one way on four of the five
        // platform heads, because only macOS has a built flower_ffmpeg
        // artifact so far (native/ffmpeg/README.md). Asking somebody to pick
        // between a decoder and a decoder that silently is not there is worse
        // than not asking. It becomes a real setting when the artifacts exist;
        // until then FLOWER_DECODER overrides it per run for A/B.
        //
        // Read once, at startup: the format the whole pipeline carries follows
        // from this, and nothing downstream of a running decoder can change
        // format.
        public TrackDecoderKind AudioDecoder { get; set; } = TrackDecoderKind.LibVlc;
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
            // browse for a folder they've already pointed Music.app at. When to do
            // that is ITunesIntegration's call, not this store's - Flower.Server
            // asks it the identical question before its own scan.
            if (Importer.ITunesIntegration.ResolveMediaFolderToAdopt(settings, _logger) is { } appleMusicFolder)
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

        // Reads LogFileMinimumLevel alone, before anything else has run. This
        // exists because of an ordering knot: AppLogging.Initialize has to be
        // the very first thing in startup (classes with a static logger field
        // bind whatever factory exists the first time they are touched), but the
        // level it should use is stored in settings.json - and a full Load()
        // logs, seeds library paths and can write the file back, none of which
        // may happen this early. So this peeks at the one field and nothing
        // else, deliberately without a logger: there is no log to write to yet,
        // and a settings file that cannot be read is already reported properly
        // by the real Load() moments later. Any failure just means the default.
        public static LogEventLevel ReadLogFileMinimumLevel()
        {
            try
            {
                using var stream = File.OpenRead(StorePath);
                using var document = JsonDocument.Parse(stream);
                if (!document.RootElement.TryGetProperty(nameof(AppSettings.LogFileMinimumLevel), out var level))
                    return LogEventLevel.Debug;

                // Both forms accepted. The property serializes as a name, but a
                // settings.json written before that converter existed - or by
                // hand, or by anything else round-tripping the enum - carries a
                // number instead, and refusing to read it would silently reset
                // somebody's chosen level to the default.
                if (level.ValueKind == JsonValueKind.String
                    && Enum.TryParse<LogEventLevel>(level.GetString(), ignoreCase: true, out var named))
                {
                    return named;
                }

                if (level.ValueKind == JsonValueKind.Number
                    && level.TryGetInt32(out var ordinal)
                    && Enum.IsDefined(typeof(LogEventLevel), ordinal))
                {
                    return (LogEventLevel)ordinal;
                }
            }
            catch
            {
                // Missing, unreadable or malformed - the default is the right
                // answer for all three, and this is not the place to say so.
            }

            return LogEventLevel.Debug;
        }

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
