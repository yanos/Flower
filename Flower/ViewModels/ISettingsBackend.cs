using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

using Flower.Logging;
using Flower.Persistence;

namespace Flower.ViewModels;

// Which parts of the settings screen make sense for a given backend. One XAML
// (SettingsPanel.axaml) renders both this device's own settings and a remote
// server's, and the difference between them is entirely which of these are true -
// so the panel binds visibility to these rather than being forked into two
// near-identical views that would then drift.
public sealed record SettingsCapabilities
{
    // Theme is a property of the app you are looking at, not of the machine you
    // are configuring: a browser tab administering a headless server has no theme
    // to set on it.
    public bool ThemePicker { get; init; }

    // macOS/Windows Music.app integration - a headless server has no Music.app,
    // and neither does a browser.
    public bool ITunesIntegration { get; init; }

    // "Send logs to paired server", and the Devices tab's server picker: the
    // things that only mean something for a device that pairs *to* a server.
    // A Flower.Server does none of them - it is the thing being paired with.
    public bool PairedServerPicker { get; init; }

    // The Devices tab's roster of devices allowed to sync with the thing being
    // configured, and the Forget/rename actions on it. Server-only for the same
    // reason: a client accepts no incoming connections, so it has no roster.
    public bool TrustedDevices { get; init; }

    // "Open App Data Location" - only meaningful when the data directory is on
    // the machine the UI is running on.
    public bool RevealAppDataLocation { get; init; }

    // Advertised host, mDNS announcement, LanGuard CIDRs, the public-access
    // switch. Server-only: the app's own equivalents are not
    // operator-configurable, and a client accepts no incoming connections at
    // all, so it has no door to open.
    public bool ServerNetwork { get; init; }

    // Issue a pairing code for a new device. Server-only - it is the end that
    // hands codes out.
    public bool PairingCodes { get; init; }

    // SYNC-PLAN.md path B - credentials for third-party Subsonic clients.
    public bool SubsonicCredentials { get; init; }

    // A Logs tab reading the log of the thing being configured.
    public bool Log { get; init; }

    // "Rebuild Database" versus a plain rescan. The app rebuilds; the server
    // rescans (its schema is migrated by FlowerDb on construction, so there is
    // nothing to rebuild).
    public bool RebuildDatabase { get; init; }
}

// Everything the settings screen reads once, when it opens. A record rather than
// a bag of out-parameters so a backend can fill it in one round trip - which for
// the remote backend is literally one GET.
public sealed record SettingsSnapshot
{
    public string Alias { get; init; } = "";
    public AppThemePreference ThemePreference { get; init; }
    public IReadOnlyList<string> LibraryPaths { get; init; } = [];
    public bool IntegrateWithITunes { get; init; }
    public bool SyncPlayCountFromITunes { get; init; }
    public bool SyncDateAddedFromITunes { get; init; }
    public bool ShareLogsWithPairedServer { get; init; }
    public string AdvertisedHost { get; init; } = "";
    public bool AdvertiseOnLan { get; init; } = true;
    public bool TrustTailscaleRange { get; init; } = true;
    public IReadOnlyList<string> AllowedCidrs { get; init; } = [];
    public bool AllowPublicAccess { get; init; }

    // Every origin the thing being configured believes it can be dialled at,
    // shown read-only. The network page is otherwise asking an operator to
    // reason about reachability with no idea what address they are reasoning
    // about - and once public access is on, this is the address to hand out.
    public IReadOnlyList<string> Addresses { get; init; } = [];

    // What the internet sees the server as, when it could be found out - see
    // PublicAddressProbe. Null on a client (nothing there asks), and null on a
    // server that has no way out or was refused an answer; the page then simply
    // does not show the line. None of the addresses above is this one: a machine
    // behind a router cannot see its own public address.
    public string? PublicAddress { get; init; }

    // Shown read-only, for the "where does this thing keep its stuff" question
    // that is otherwise unanswerable about a machine you are not sitting at.
    public string DataDirectory { get; init; } = "";
    public string? Version { get; init; }

    // Where play counts would actually come from, described without doing the
    // slow work of exporting them (see SettingsWindow.DescribeITunesLibrarySource).
    public string ITunesLibraryDescription { get; init; } = "";

    // Music.app's configured media folder, when there is one. The iTunes master
    // switch adds/removes it from the pending library-paths list.
    public string? AppleMusicFolder { get; init; }

    // Whether this device is currently paired to a server. Its library is then
    // synced in rather than locally curated, so the library-folder controls are
    // disabled - see SettingsViewModel.CanManageLibrary.
    public bool IsPairedToServer { get; init; }
}

// The subset the OK button writes back. Only what the user can actually edit -
// DataDirectory, Version and the iTunes description are read-only above.
public sealed record SettingsDraft
{
    public required string Alias { get; init; }
    public required AppThemePreference ThemePreference { get; init; }
    public required IReadOnlyList<string> LibraryPaths { get; init; }
    public required bool LibraryPathsChanged { get; init; }
    public required bool IntegrateWithITunes { get; init; }
    public required bool SyncPlayCountFromITunes { get; init; }
    public required bool SyncDateAddedFromITunes { get; init; }
    public required bool ShareLogsWithPairedServer { get; init; }
    public required string AdvertisedHost { get; init; }
    public required bool AdvertiseOnLan { get; init; }
    public required bool TrustTailscaleRange { get; init; }
    public required IReadOnlyList<string> AllowedCidrs { get; init; }
    public required bool AllowPublicAccess { get; init; }
}

// What a settings screen can do, independent of whether "settings" means this
// device's own (LocalSettingsBackend) or a Flower server's, reached over its admin
// API (RemoteServerSettingsBackend).
//
// Methods whose Capabilities flag is false are never called and may throw
// NotSupportedException - the panel does not render the control that would call
// them. That is deliberately not "return a harmless default": a backend silently
// no-opping an action the user can see and click is the failure mode this
// interface exists to prevent.
public interface ISettingsBackend
{
    SettingsCapabilities Capabilities { get; }

    Task<SettingsSnapshot> LoadAsync(CancellationToken ct = default);

    // Returns a human-readable note about anything that did not take effect
    // immediately (a server alias needs a restart to be re-announced over mDNS),
    // or null when there is nothing to say.
    Task<string?> SaveAsync(SettingsDraft draft, CancellationToken ct = default);

    // How many of the library's tracks live under this folder, or -1 for "cannot
    // say cheaply" (see LibraryPathRow.SongCount).
    int CountSongsUnder(string folder);

    Task<IReadOnlyList<TrustedPeerRow>> LoadDevicesAsync(CancellationToken ct = default);
    Task<IReadOnlyList<DeniedPeerRow>> LoadDeniedDevicesAsync(CancellationToken ct = default);
    Task ForgetDeviceAsync(TrustedPeerRow device, CancellationToken ct = default);
    Task ForgetDenialAsync(DeniedPeerRow device, CancellationToken ct = default);

    Task<string> IssuePairingCodeAsync(bool grantsAdmin, CancellationToken ct = default);

    Task<IReadOnlyList<SubsonicCredentialRow>> LoadSubsonicCredentialsAsync(CancellationToken ct = default);
    Task<SubsonicCredentialRow> IssueSubsonicCredentialAsync(string label, CancellationToken ct = default);
    Task RevokeSubsonicCredentialAsync(SubsonicCredentialRow credential, CancellationToken ct = default);

    // Kicks off a library rescan and returns once it has *started*, not once it
    // has finished - a full scan outlasts any sensible request timeout, and on the
    // local backend it deliberately runs unawaited behind the app's busy spinner.
    Task RescanAsync(CancellationToken ct = default);
    Task RebuildDatabaseAsync(CancellationToken ct = default);

    // This server's own log. Structured entries rather than rendered lines:
    // the Logs tab is a real log viewer now (see LogViewerViewModel), and its
    // minimum-level filter needs each entry's level, not a string that happens
    // to start with one.
    //
    // afterSequence is a cursor into the log's own numbering, so the tab can
    // follow a live log by asking every couple of seconds for nothing but what
    // has been logged since - InMemoryLogStore.BeforeFirstSequence for the
    // first read, then the LastSequence of the slice before. The returned
    // LastSequence is the log's high-water mark, not the last entry returned:
    // anything in between fell out of the ring and is not coming back.
    Task<LogSlice> LoadLogAsync(int limit, long afterSequence, CancellationToken ct = default);

    // One paired device's own log, as last pushed to the server at the end of a
    // sync (see AppSettings.ShareLogsWithPairedServer on the pushing side). The
    // point of the feature is that the person who runs the server is the one who
    // ends up diagnosing a listener's phone, and the listener cannot be talked
    // through finding a log file.
    //
    // Null for a device that has never pushed anything - distinct from an empty
    // list (a device that pushed a log with nothing in it), and rendered as a
    // sentence rather than as a blank pane.
    Task<IReadOnlyList<InMemoryLogEntry>?> LoadDeviceLogAsync(string fingerprint, int limit, CancellationToken ct = default);
}
