using System.Collections.Generic;

namespace Flower.Services;

// The settings half of Flower.Server's /api/admin wire shape, declared once for
// both ends of it: Flower.Server answers with these (AdminEndpoints' GET and PUT
// /settings) and ServerAdminClient reads them, the same way LibrarySyncContracts
// is shared by the sync endpoint and its client.
//
// The rest of that surface is still a matching pair of records - the server's own
// in AdminEndpoints, the client's in ServerAdminClient - because neither side has
// had to touch them in step with the other. These two are different: every
// operator-editable setting is a field in both, so a setting added to the server
// is a field added twice, and the browser page that renders them is built from
// the *client's* copy. Sharing them makes that one edit again.
//
// Nullable on the update means "leave this one alone" (AdminEndpoints tests each
// with `is { }`), which is why every field there is optional and none of them
// carry a default: a settings screen sends the whole draft, but a script poking
// one value should not have to restate the rest.
public sealed record ServerSettingsDto(
    string Alias,
    string AdvertisedHost,
    bool AdvertiseOnLan,
    bool TrustTailscaleRange,
    List<string> AllowedCidrs,
    List<string> LibraryPaths,
    bool IntegrateWithITunes,
    bool SyncPlayCountFromITunes,
    bool SyncDateAddedFromITunes,
    // Music.app's configured media folder, or null when the server has none to
    // find - it is not a Mac, or Music.app was never set up on it. The settings
    // page disables the three switches above and says so when this is null,
    // rather than offering switches that could not do anything.
    string? AppleMusicFolder,
    // One line describing where those two imports would read from, without doing
    // the export - see ITunesIntegration.DescribeSource.
    string ITunesLibraryDescription,
    // Shown read-only, for the "where does this thing keep its stuff" question
    // that is otherwise unanswerable about a machine you are not sitting at.
    string DataDirectory,
    string? Version,
    // Whether this server answers callers from outside the LAN at all - see
    // FlowerServerOptions.AllowPublicAccess, which is the setting this is.
    bool AllowPublicAccess,
    // Every origin this server believes it can be dialled at right now, the same
    // list /info hands a client (DiscoveryEndpoints.ReachableOrigins). Read-only,
    // and the answer to the two questions the network page otherwise cannot
    // answer about a machine you are not sitting at: what an empty advertised
    // address resolves to, and which address to hand out once the door is open.
    List<string> Addresses,
    // What the internet sees this server as, or null when nothing could be
    // found out - see PublicAddressProbe. Read-only, and shown next to the
    // switch that opens the door, because that is the moment it is needed: a
    // machine behind a router holds none of the addresses above from outside,
    // so none of them answers "what do I forward to, and what do I then type
    // into a phone that is not on this network".
    string? PublicAddress,
    // The fields whose new value is on disk and bound but not yet acted on, so
    // the page can say so instead of appearing to have done nothing:
    // MdnsAdvertiser reads its options once, when the hosted service starts.
    List<string>? RestartRequired = null,
    // This server's own identity fingerprint, read-only. It is the other half
    // of a check a paired device can only half-make on its own: the device
    // shows what it pinned, and the answer to "is that the right machine?"
    // has to come from the machine itself, over a channel that is not the one
    // being checked - a screen someone is looking at. Matters most for a
    // device paired with a bare code, which had nothing to verify at the time
    // (see PairingEntry).
    string? Fingerprint = null,
    // The public address this server advertises on its own, as the origin a
    // client is told to dial - null unless AllowPublicAccess is on and
    // AdvertisedHost is empty (see PublicReachability) - and what dialling it
    // from here found: "Reachable", "Unreachable" or "OtherServerAnswered".
    // A string rather than an enum so this record needs nothing new from
    // either end's serializer context.
    string? PublicOrigin = null,
    string? PublicReachability = null);

public sealed record ServerSettingsUpdateDto(
    string? Alias,
    string? AdvertisedHost,
    bool? AdvertiseOnLan,
    bool? TrustTailscaleRange,
    List<string>? AllowedCidrs,
    List<string>? LibraryPaths,
    bool? IntegrateWithITunes,
    bool? SyncPlayCountFromITunes,
    bool? SyncDateAddedFromITunes,
    bool? AllowPublicAccess);

// POST /api/admin/library/remove - "Remove from Library" from an admin device,
// carried out on the server's own library (see LibraryRemoval). TrackIds are
// the server's catalog ids (TrackDto.Id, a client's Track.OriginTrackId).
// DeleteFiles deletes the files from the server's disk as well; without it they
// stay, and the server's scans leave them out from then on.
public sealed record LibraryRemovalRequestDto(List<string> TrackIds, bool DeleteFiles);

// Removed counts the ids that were in the library; an id that was not is not an
// error, since another device may have removed the same song a moment earlier.
// FilesTrashed went to the server's trash, FilesDeleted are gone for good (a
// server on a platform with no trash), FilesNotDeleted are still where they
// were - most often a read-only music mount, which is how docker-compose.yml
// mounts it.
public sealed record LibraryRemovalResponseDto(int Removed, int FilesTrashed, int FilesDeleted, int FilesNotDeleted);

// GET /api/admin/library/removed - the server's "Removed Songs": files an admin
// removed from the library and kept on disk, which its scans leave out (see
// Library.ExcludedPaths). StillOnDisk is false for one deleted by hand since,
// whose Restore only tidies the list.
public sealed record RemovedFileDto(string Path, System.DateTimeOffset RemovedAt, bool StillOnDisk);

// POST /api/admin/library/removed/restore - takes these off the list and starts
// a rescan, which is what brings the songs back. Restored counts the paths that
// were on the list.
public sealed record RestoreRemovedFilesRequestDto(List<string> Paths);

public sealed record RestoreRemovedFilesResponseDto(int Restored);

