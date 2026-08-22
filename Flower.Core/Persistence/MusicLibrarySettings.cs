using System.Collections.Generic;

namespace Flower.Persistence;

// The settings every Flower host that owns a music library has, whatever else it
// is: which folders to scan, and how much of a local iTunes/Music.app
// installation to adopt. `AppSettings` (the app, persisted as settings.json) and
// `FlowerServerOptions` (the server, bound from configuration) both derive from
// this, so the four below are declared, defaulted and documented exactly once.
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

    // Master switch for every way Flower reaches into a local iTunes/Music.app
    // installation: adopting Music.app's own configured media folder as a
    // library path (see ITunesIntegration.ResolveMediaFolderToAdopt) and the two
    // per-track imports below. On by default - on a Mac with a Music.app
    // library, having Flower pick that library up on its own is what most people
    // want; turning this off makes Flower ignore Music.app entirely and leaves
    // the library purely whatever folders were added by hand. The two flags
    // below stay independently meaningful (and keep their own persisted values)
    // but are inert while this is false.
    public bool IntegrateWithITunes { get; set; } = true;

    // Whether to import per-track play counts from iTunes/Music.app's library
    // export on every scan - see ITunesPlayCountImporter and
    // Track.ImportedPlayCount. On by default; a harmless no-op when no such
    // export exists, and on every non-macOS host.
    public bool SyncPlayCountFromITunes { get; set; } = true;

    // Same export, but for Track.DateAdded instead of play counts - see
    // ITunesDateAddedImporter. On by default, like the two above: where
    // Music.app has an older record of when a file entered a library, that date
    // is the more truthful one (the older of the two always wins - see that
    // importer's own doc comment), and a Recently Added view built on it is the
    // one the user already recognises. This used to default off in the app, to
    // avoid reordering Recently Added under anyone who updated - with no
    // released users there is nobody that could happen to, and defaulting it off
    // meant the useful half of the integration was the half nobody had on.
    public bool SyncDateAddedFromITunes { get; set; } = true;
}
