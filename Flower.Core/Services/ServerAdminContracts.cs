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
    // The name this server is actually announced under, when it is not
    // Alias: another server on the network already answered to Alias, so this
    // one took "Alias (2)" (see MdnsAdvertiser). Null when they are the same.
    // Kept apart from Alias because Alias is the editable value - folding the
    // suffix into it would save "(2)" into the settings the next time the page
    // was submitted.
    string? AdvertisedAs = null,
    // Settings the page cannot change, each with what sets it instead - "the
    // Flower__Alias environment variable" - for the page to grey the field out
    // and say why. Something ranked above flower-server.json (the environment,
    // the command line) wins over anything saved here; see SettingsOverrides.
    Dictionary<string, string>? Overridden = null,
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
// were - most often a music folder the server cannot write to.
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


// POST /api/admin/library/uploads - an admin device offering the server a file
// of its own that the server does not have (see LibraryIngest, and
// LibraryMirrorService for the offering end).
//
// RelativePath is the file's path below the device's own library folder,
// '/'-separated - TrackDto.RelativePath in the other direction - and is where
// the file goes below the server's. Sha256 and Length are of the whole file:
// they are what the server stages the pieces under, what lets a cut-off upload
// resume, and what the assembled file is held to before it is moved into
// place. DateAdded is when the device first had the song, so a library that
// has been on a desktop for years does not arrive on the server dated today.
//
// ReplacesTrackId is set when the file is a new version of a song the server
// already has - the device's copy has changed since the server got it, most
// often because its tags were edited, and tags live in the file. The server
// then puts these bytes where that song's file is, whatever RelativePath says,
// and the song keeps its id and everything known about it.
public sealed record LibraryUploadRequestDto(
    string RelativePath, long Length, string Sha256, System.DateTimeOffset DateAdded, string? ReplacesTrackId = null);

// The answer to that, and to each PUT /api/admin/library/uploads/{id}?offset=
// that follows with the next piece.
//
// While UploadId is set the server wants more, starting at Offset - which is
// wherever *it* got to, not wherever the device thinks it did, so a device
// resuming after a dropped connection sends from here and nothing twice.
// Once the file is in the library UploadId is null and TrackId is the song:
// the server's id for it, which the device records as Track.OriginTrackId.
// DateAdded is what the server settled on (the older of the device's and its
// own record, if it has had the song before), and LibraryToken the catalog
// token this upload produced, handed back for the reason
// TrackStateReportHeaders gives - so the device does not mistake its own
// change for news and pull the whole catalog to learn about it.
//
// TagsEditedAt is set when the file was a new version of a song and its tags
// differ from the old one's: the server dates that as an edit, so every other
// device takes the new tags, and tells the sender the date so that the sender -
// whose tags these already are - does not take them back as news.
public sealed record LibraryUploadStatusDto(
    string? UploadId,
    long Offset,
    string? TrackId = null,
    System.DateTimeOffset? DateAdded = null,
    string? LibraryToken = null,
    System.DateTimeOffset? TagsEditedAt = null,
    // The same for the picture: set when the new version's artwork differs.
    System.DateTimeOffset? ArtEditedAt = null,
    // And for the file itself, always set for a new version: the date every
    // other device will find newer than its copy, and the sender must not.
    System.DateTimeOffset? FileReplacedAt = null);

// POST /api/admin/library/move - an owner's device has moved or renamed one of
// its own files, and the server's copy follows it. TrackId is the server's id
// for the song; RelativePath is where the file now sits below the device's
// library folder, and so where it goes below the folder the server keeps it
// under. See Track.MovedFromPath for why this is only ever sent for a change.
public sealed record LibraryMoveRequestDto(string TrackId, string RelativePath);

// Where the server's file ended up, relative to its library folder, and the
// catalog token the move produced (see LibraryUploadStatusDto.LibraryToken).
public sealed record LibraryMoveResponseDto(string RelativePath, string? LibraryToken = null);

// POST /api/admin/library/removed/delete - the other way out of "Removed
// Songs": the files named are deleted from the server's disk for good, where
// Restore puts their songs back. Only paths on that list are touched, so the
// one thing a caller can delete is a file an admin already removed.
public sealed record DeleteRemovedFilesRequestDto(List<string> Paths);

// Deleted counts the files that are gone (one that was already missing counts:
// the outcome is the one asked for). NotDeleted are still there and still on
// the list.
public sealed record DeleteRemovedFilesResponseDto(int Deleted, int NotDeleted);
