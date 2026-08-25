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
    // The fields whose new value is on disk and bound but not yet acted on, so
    // the page can say so instead of appearing to have done nothing:
    // MdnsAdvertiser reads its options once, when the hosted service starts.
    List<string>? RestartRequired = null);

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
