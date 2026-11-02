using System.Collections.Generic;
using System.Text.Json.Serialization;

using Flower.SemanticAudio;

namespace Flower.Persistence;

// The settings every Flower host that owns a music library has, whatever else it
// is: which folders to scan, and which of Music.app's own records to import
// over them. `AppSettings` (the app, persisted as settings.json) and
// `FlowerServerOptions` (the server, bound from configuration) both derive from
// this, so the three below are declared, defaulted and documented exactly once.
//
// What is *not* here is whether to take Music.app's media folder as a library
// folder at all, because the two hosts settle that differently: the app asks
// once, on its first run (ITunesLibraryOffer), and the server has a switch
// (FlowerServerOptions.IntegrateWithITunes).
//
// A base class rather than a nested "Library" property on each: these are read
// as `settings.LibraryPaths` from roughly everywhere, and a nesting level buys
// nothing that inheritance doesn't already give. It is also what lets the shared
// policy in ITunesIntegration take one parameter and serve both hosts.
//
// Deliberately only the overlap. The rest of AppSettings is UI state the server
// has no notion of (window geometry, column widths, the theme) and the rest of
// FlowerServerOptions is deployment configuration the app has no notion of (a
// data directory chosen by an operator, mDNS and LanGuard settings) - and the
// two are populated by mechanisms that don't unify either: one is a JSON file
// this process owns and rewrites, the other is an IConfiguration stack of
// appsettings.json, a settings file, environment variables and command-line
// switches. Only this much is genuinely the same thing on both.
public class MusicLibrarySettings
{
    public List<string> LibraryPaths { get; set; } = [];

    // Whether to import per-track play counts from iTunes/Music.app's library
    // export on every scan - see ITunesPlayCountImporter and
    // Track.ImportedPlayCount. On by default; a harmless no-op when no such
    // export exists, and on every non-macOS host.
    public bool SyncPlayCountFromITunes { get; set; } = true;

    // Same export, but for Track.DateAdded instead of play counts - see
    // ITunesDateAddedImporter. On by default, like the one above: where
    // Music.app has an older record of when a file entered a library, that date
    // is the more truthful one (the older of the two always wins - see that
    // importer's own doc comment), and a Recently Added view built on it is the
    // one the user already recognises. This used to default off in the app, to
    // avoid reordering Recently Added under anyone who updated - with no
    // released users there is nobody that could happen to, and defaulting it off
    // meant the useful half of the integration was the half nobody had on.
    public bool SyncDateAddedFromITunes { get; set; } = true;

    // Whether this host looks things up on the internet for the songs it owns
    // - for now a picture of each artist, from Deezer (OnlineMetadataEngine,
    // docs/ONLINE-METADATA-PLAN.md). On by default: it asks for no key and no
    // account, and what it sends is an artist's name. Here because it is the
    // same switch on both hosts and means the same thing - with one
    // difference in reach: on a server it covers every device paired to it,
    // which ask the server rather than the internet, and a paired device's own
    // value is never consulted (AppSettings.PairedServerLooksUpOnline is).
    public bool LookUpOnline { get; set; } = true;

    // The master switch over every AI feature - a model this host runs on
    // its own songs, for now only Vibe Playlists (SemanticAudioEngine,
    // docs/VIBE-PLAYLIST-PLAN.md). Off by default: turning it on downloads a
    // 330 MB model and has this host listen to every song it owns, which is
    // hours of a CPU, so it is the owner's to ask for. Turning it off deletes
    // every model and everything they worked out. On a server it covers every
    // device paired to it, and a paired device's own value is never consulted.
    public bool AiFeaturesEnabled { get; set; }

    // Each AI feature's own switch, which counts only while AiFeaturesEnabled
    // is on. On by default, so the master switch turned on is every feature
    // on; turning one off deletes what only it used, as the master does.
    public bool VibeEnabled { get; set; } = true;

    // Whether Vibe Playlists run here: both switches. Not a setting, so not
    // in settings.json either.
    [JsonIgnore]
    public bool RunsVibePlaylists => AiFeaturesEnabled && VibeEnabled;

    // How many 10 s clips of each song are listened to: a count spread evenly
    // over the song, or 0 for all of it (ClipGrid.WholeSong). See
    // ClipGrid.Settings for the choices offered.
    public int VibeClipsPerSong { get; set; } = ClipGrid.DefaultClipsPerSong;
}
