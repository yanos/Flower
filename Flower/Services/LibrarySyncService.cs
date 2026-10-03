using System;
using System.Threading;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;

using Microsoft.Extensions.Logging;

using Flower.Importer;
using Flower.Logging;
using Flower.Models;
using Flower.Persistence;

namespace Flower.Services;

// Outcome of one SyncWithAsync call - lets a user-initiated caller (see
// MainViewModel.ForceSyncNow) report something more useful than silence when
// nothing visibly changes: "reached the peer but already up to date" and
// "couldn't reach the peer at all" both merge zero new tracks, but they're
// very different things to tell the user.
// Unchanged means the peer answered 304 to our conditional request (see
// LibrarySyncService's _lastSeenTokens): its catalog is byte-for-byte what we
// already merged, so nothing was fetched and nothing needed merging. That is
// a success, not a failure - distinguished only so a user-initiated sync can
// say "already up to date" rather than implying it re-pulled everything.
//
// Failure says which kind of "no" this was, and exists because the distinction
// the comment above draws for success was never drawn for failure: every way a
// sync can fail returned the same bare false, so the one sentence the user got
// was "could not reach it - check it's still on the network and paired". A
// server that was reached, answered promptly, and said 429 was reported as a
// server that was not there, next to a connection icon correctly showing it
// was - and the advice was to go and check the network, which was fine.
public readonly record struct LibrarySyncResult(
    bool Success,
    int FetchedCount,
    int AddedCount,
    bool Unchanged = false,
    SyncFailure Failure = SyncFailure.None);

// Why a sync did not happen, in the terms the user's next action depends on:
// wait, re-pair, or go and look at the network.
public enum SyncFailure
{
    None,

    // Nothing answered - refused connection, timeout, no route. The only one of
    // these that is actually about the network.
    Unreachable,

    // 429. The peer is right there and asked us to slow down; the budget rolls
    // over on its own, so the answer is to wait rather than to change anything.
    Throttled,

    // 403. Reached, and refused: this device is not (or no longer) trusted by
    // that server. See PeerTrustRejected, raised alongside.
    NotTrusted,

    // Reached, and answered with something else that was not a success - a 500,
    // a malformed catalog, an endpoint that isn't there. Nothing the user can
    // act on beyond looking at the log, which is what the message says.
    Refused,
}

// Pulls a peer's full track catalog in one request (GET /api/flower/v1/library
// - see LibrarySyncContracts) and merges anything this device doesn't already
// have as Path == null placeholders - see SYNC-PLAN.md Phase 3. Talks to the
// server's bulk endpoint directly (same signed identity headers as
// PlaylistSyncService - see Flower.Server's SyncEndpoints) rather than through
// PeerMediaClient: an earlier version used the OpenSubsonic-shaped
// getAlbumList2/getAlbum pair, one request per album, which for a library of
// hundreds/thousands of albums meant hundreds/thousands of individual
// connections in a burst - observed in practice as heavy iOS nw_connection log
// churn. PeerMediaClient itself is unaffected and still used for stream and
// download.
//
// Originally both sides of a discovered pair ran this independently rather
// than electing one initiator - there's no write-back to the peer here, just
// a local, additive merge, so there was no risk of two conflicting writes
// racing, and in the old mesh model both sides genuinely needed to learn
// about the other's exclusive tracks. Under Client/Server roles (see
// SyncRolePolicy) this method is only ever called by a Client pulling from
// its one paired Server - a Server's own trigger paths (MainViewModel) are
// gated off entirely, so it never calls this at all, making the pull
// effectively one-directional (client-pulls-from-server) without needing any
// change to this method itself.
public class LibrarySyncService
{
    // A real library's manifest can run into the tens of thousands of songs
    // (observed: 16k+ tracks) - PlaylistSyncService's 10s timeout is fine for
    // its much smaller payload, but this one needs enough headroom for a much
    // bigger JSON response over a possibly-imperfect WiFi link without silently
    // timing out and aborting the whole sync (see the catch below).
    //
    // Through PeerHttpClient, not `new HttpClient()`: the endpoint this dials
    // is whichever address ranks best for the paired server, and among equal
    // addresses that is the https one (see NetworkDiscoveryService.PickBest).
    // A self-hosted server serves that with its own self-signed certificate,
    // which only the pinning callback accepts - a bare client refuses every
    // request to it, silently and forever, while discovery, art and playback
    // (all already pinned) carry on working.
    private static readonly HttpClient Http = PeerHttpClient.Create(TimeSpan.FromMinutes(2));

    // The ETag (Library.ChangeToken) each peer served with the manifest we
    // last successfully merged from it, sent back as If-None-Match so an
    // unchanged catalog costs one 304 instead of 6-8 MB - see
    // SyncEndpoints' GET /library, ARCHITECTURE-REVIEW Tier 1.4.
    // In-memory only: the token is session-scoped on the serving side anyway
    // (see Library.ChangeToken), so persisting it would buy nothing.
    private readonly ConcurrentDictionary<string, string> _lastSeenTokens = new();

    // Per peer, the library token that peer reported back after merging this
    // device's own last track-state push - see TrackStateReportHeaders for why
    // one is handed back at all. Not persisted, and not a substitute for
    // _lastSeenTokens: this says "that value is my own echo", never "that
    // catalog is already merged here".
    private readonly ConcurrentDictionary<string, string> _tokensThisDeviceCaused = new();

    // Whether a token a peer is now advertising is one this device's own
    // reporting produced a moment ago. Virtual for the same reason the sync
    // entry points are: a coordinator test drives this seam without a server.
    public virtual bool CausedPeerLibraryToken(string fingerprint, string token) =>
        !string.IsNullOrEmpty(token) && _tokensThisDeviceCaused.GetValueOrDefault(fingerprint) == token;

    // The other thing this device does that moves a peer's token: handing it a
    // file (see LibraryMirrorService). Recorded for the reason a track-state
    // report's is - an upload of a hundred songs is a hundred token changes,
    // and each would otherwise read as news worth a whole catalog.
    public void NoteLibraryTokenCausedHere(string fingerprint, string? token)
    {
        if (!string.IsNullOrEmpty(fingerprint) && !string.IsNullOrEmpty(token))
            _tokensThisDeviceCaused[fingerprint] = token;
    }

    // Whether what this device holds about that peer's catalog is what the
    // peer last served, merged against the library as it is now. False before
    // the first pull of a session, and false again once the library changes
    // underneath it.
    //
    // It is what stands between "a file the server does not have" and "a file
    // nobody has asked the server about yet". Both look the same on the track -
    // no origin - and only the first is something to upload.
    public virtual bool HasCurrentCatalogFrom(string fingerprint) =>
        !string.IsNullOrEmpty(fingerprint) && _lastSeenTokens.ContainsKey(fingerprint);

    // Set around this service's own merge, on the thread doing it - which is
    // the thread Library raises LibraryChanged on, synchronously - so the
    // handler below can tell that change from everyone else's.
    [ThreadStatic]
    private static bool t_mergingPulledCatalog;

    // A scan found files, or songs were removed: whatever was matched against
    // the peer's catalog was matched against a library that no longer exists.
    // Forgetting the tokens makes the next pull a real one rather than a 304,
    // which is what re-runs the matching - a rescan's new files are compared
    // with what the server has before any of them is offered to it.
    private void OnLibraryChanged(object? sender, EventArgs e)
    {
        if (!t_mergingPulledCatalog)
            _lastSeenTokens.Clear();
    }

    private readonly Library _library;
    private readonly DeviceIdentity _deviceIdentity;
    private readonly IPeerCredentials _credentials;

    // Kept for the one thing here that fetches a whole file - a new version
    // of a song this device has a copy of (ApplyNewerFilesAsync) - which goes
    // through PeerMediaClient like any other download.
    private readonly DeviceSigningKey _signingKey;
    private readonly AppSettings _appSettings;
    private readonly ServerStarBaselineStore _starBaselines;
    private readonly DeviceLogArchive _logArchive;
    private readonly ILogger _logger;

    // The importer's own, injected rather than reused: it is a separate class
    // with its own category, and the browser resolves it from the container the
    // same way - this service just happens to construct one per peer, since a
    // peer's address is only known per call.
    private readonly ILogger<RemoteLibraryImporter> _importerLogger;

    // See PeerTrustRejectedEventArgs (PlaylistSyncService.cs) - same trust gate,
    // same meaning here.
    public event EventHandler<PeerTrustRejectedEventArgs>? PeerTrustRejected;

    // For the importer this service builds to read the files it downloads.
    private readonly ILogger<Flower.Importer.Importer> _scanLogger;

    public LibrarySyncService(Library library, DeviceIdentity deviceIdentity, DeviceSigningKey signingKey, AppSettings appSettings, ServerStarBaselineStore starBaselines, DeviceLogArchive logArchive, ILogger<LibrarySyncService> logger, ILogger<RemoteLibraryImporter> importerLogger, ILogger<Flower.Importer.Importer> scanLogger)
    {
        _scanLogger = scanLogger;
        _library = library;
        _deviceIdentity = deviceIdentity;
        // Constructed here rather than injected: every caller that could supply
        // one would build it from exactly these three, which the container
        // already hands this service.
        _credentials = new SignedDeviceCredentials(deviceIdentity, signingKey);
        _signingKey = signingKey;
        _appSettings = appSettings;
        _starBaselines = starBaselines;
        _logArchive = logArchive;
        _logger = logger;
        _importerLogger = importerLogger;

        // For the life of the library, which is the life of this service: both
        // are singletons of the same container.
        library.LibraryChanged += OnLibraryChanged;
    }

    // Virtual for the same reason PeerTrackResolver.Resolve is: it is the seam
    // a test needs to drive PeerSyncCoordinator.ForceSyncNowAsync's *reachable*
    // path - the result strings, the trust confirmation - without standing up
    // a real peer to sync against. See docs/ARCHITECTURE-REVIEW.md Tier 5.6.
    public virtual async Task<LibrarySyncResult> SyncWithAsync(DiscoveredDevice device)
    {
        if (string.IsNullOrEmpty(device.Fingerprint))
        {
            _logger.LogTrace("Library sync skipped for {Alias}: no resolved fingerprint yet", device.Alias);
            return new LibrarySyncResult(false, 0, 0);
        }

        _logger.LogInformation("Library sync starting with {Alias} ({Fingerprint}) at {EndPoint}",
            device.Alias, device.Fingerprint, device.BaseUri);

        List<Track> placeholders;
        IReadOnlySet<string> removedAtServer;
        string? servedToken;
        int fetchedCount;
        try
        {
            // The request itself is RemoteLibraryImporter's - the same class the
            // browser head uses as its whole library - so there is one HTTP path
            // to this endpoint instead of two that have to be kept in step. What
            // stays here is what is genuinely this service's own: the per-peer
            // token cache below, the trust-rejection signal, and the additive
            // merge into Library.
            var importer = new RemoteLibraryImporter(
                Http, device.Origin, _credentials,
                originFingerprint: device.Fingerprint, ownFingerprint: _deviceIdentity.Fingerprint,
                _importerLogger, closeConnection: true, originPublicKey: device.PublicKey);

            var fetch = await importer.FetchAsync(_lastSeenTokens.GetValueOrDefault(device.Fingerprint));
            if (fetch.NotModified)
            {
                _logger.LogTrace("Library sync with {Alias}: catalog unchanged since {Token}, nothing to merge",
                    device.Alias, fetch.ETag);
                // Nothing new to merge - but a new version of a file that was
                // held back for home last time is still owed, and this may be
                // home.
                await ApplyNewerFilesAsync(device, []);
                return new LibrarySyncResult(true, 0, 0, Unchanged: true);
            }

            placeholders = fetch.Tracks;
            removedAtServer = fetch.RemovedIds;
            servedToken = fetch.ETag;
            fetchedCount = placeholders.Count;
        }
        catch (Exception ex)
        {
            // Peer unreachable, not running this endpoint yet, or not (yet) trusted.
            _logger.LogWarning(ex, "Library sync with {Alias} ({Fingerprint}): GET /library failed, aborting this sync attempt",
                device.Alias, device.Fingerprint);

            // See PlaylistSyncService's identical check - device-unknown here
            // means the same thing, just from this service's own (also
            // trust-gated) first request.
            if (PeerResponses.IsDeviceUnknown(ex))
                PeerTrustRejected?.Invoke(this, new PeerTrustRejectedEventArgs { Fingerprint = device.Fingerprint, Alias = device.Alias });

            return new LibrarySyncResult(false, 0, 0, Failure: Classify(ex));
        }

        _logger.LogInformation("Library sync with {Alias}: fetched {SongCount} song(s) from their catalog", device.Alias, fetchedCount);

        // No early-return for an empty catalog: a peer reporting zero songs
        // (its whole library emptied, or a fresh pairing to one with nothing
        // yet) must still prune every not-yet-downloaded placeholder this
        // device previously learned from it - see Library.MergeSyncedTracks.
        var beforeCount = _library.Tracks.Count;
        // Copies this device downloaded of songs the server has since removed.
        // The merge below takes each out of the library and sets its file
        // aside, as it does for any removed song - and for a download that is
        // one step short. The file was never this device's own, only the
        // server's song lent, and the server still has the original set aside
        // for its owner to restore or delete; so the lent copy just goes,
        // rather than sit on a phone that has no screen to clean it up from.
        var lentAndRemoved = removedAtServer.Count == 0
            ? []
            : _library.Tracks
                .Where(t => t is { IsLocallyDownloaded: true, Path: not null, OriginTrackId: not null }
                            && t.OriginDeviceFingerprint == device.Fingerprint
                            && removedAtServer.Contains(t.OriginTrackId))
                .Select(t => t.Path!)
                .ToList();

        // What the server has changed about songs this device holds a file
        // of - newer tags, newer artwork, a file moved - collected by the
        // merge and carried out just below.
        var fileWork = new SyncedFileWork();

        int removedCount;
        t_mergingPulledCatalog = true;
        try
        {
            // An admin device keeps the older Date Added and tells the server
            // (PushTrackStateAsync below), and lets go of its own copy of a
            // song the server says was removed; everyone else mirrors the
            // server's dates and keeps the files they imported themselves.
            removedCount = _library.MergeSyncedTracks(
                device.Fingerprint, placeholders, _starBaselines.Load(device.Fingerprint),
                ownersDevice: device.WeAreAdmin, removedAtSource: removedAtServer,
                fileWork: fileWork);
        }
        finally
        {
            t_mergingPulledCatalog = false;
        }

        DeleteLentCopies(lentAndRemoved);
        ApplyMoves(fileWork.Moved);
        await ApplyNewerFilesAsync(device, fileWork.NewerFile);
        ApplyNewerTags(fileWork.NewerTags);
        await ApplyNewerArtAsync(device, fileWork.NewerArt);
        foreach (var track in fileWork.StaleArt)
            AlbumArtLoader.Invalidate(track);
        // What the server said, now the baseline for the next pull - including
        // the tracks just merged the other way, whose local star the push below
        // is about to report and ApplyAsync will then record as agreed.
        await _starBaselines.ReplaceAsync(device.Fingerprint, placeholders
            .Where(t => t.Starred && t.OriginTrackId is { Length: > 0 })
            .Select(t => t.OriginTrackId!));
        var addedCount = _library.Tracks.Count - beforeCount + removedCount;
        _logger.LogInformation("Library sync with {Alias}: merged catalog, {AddedCount} new placeholder(s) added, {RemovedCount} stale placeholder(s) pruned ({TotalBefore} -> {TotalAfter})",
            device.Alias, addedCount, removedCount, beforeCount, _library.Tracks.Count);

        // The merge above persisted itself (see Library's ITrackStore).
        // Without that a merge only lived in memory, and a killed/relaunched
        // app (mobile has no always-on background process) lost every
        // not-yet-downloaded placeholder learned this way until the next
        // successful sync - which is exactly the kind of "the caller forgot"
        // bug that moving the write into Library removes by construction.

        // Only after the merge *and* the save have both succeeded - remembering
        // the token any earlier would mean a failure between fetch and persist
        // leaves this device claiming to have content it never stored, and the
        // next sync would be answered 304.
        if (servedToken != null)
            _lastSeenTokens[device.Fingerprint] = servedToken;

        // Piggybacks log sharing on this exact sync session, so it fires "at
        // the same time as the library" with no extra caller-side wiring - the
        // server serves what lands here back from the Logs tab of its own
        // settings screen (see SettingsViewModel). Defense-in-depth, not reliance on the caller's own gating
        // (see SyncRolePolicy's doc comment above): a Server must never push
        // logs to anything, only a Client pushes its own snapshot to its one
        // paired Server.
        //
        // ShareLogsWithPairedServer gates it on top of the role check: the
        // snapshot travels over plaintext HTTP and carries exception text and
        // absolute file paths, so it ships off by default (see the setting's
        // own comment in AppSettingsStore).
        // Likewise piggybacked, and deliberately ahead of the logs: this is
        // the one thing a client learns that its server cannot learn any other
        // way. The server serves the catalog and everything in it, but it never
        // pulls, so a track played, starred or configured here is a change it
        // would otherwise never hear about - see TrackStateDto.
        //
        // After the merge, not before, and the ordering is load-bearing: the
        // seed below is what makes the push able to tell "the user changed
        // this" from "this device simply has a value", so a push that ran
        // first would have nothing to compare against.
        SeedKnownServerState(device.Fingerprint, placeholders);
        // A fresh baseline makes every track worth another look, including any
        // a push since launch passed over for having none.
        MarkAllTrackStateChanged();
        await PushTrackStateAsync(device);

        if (_appSettings.ShareLogsWithPairedServer)
            await PushLogSnapshotAsync(device);

        return new LibrarySyncResult(true, fetchedCount, addedCount);
    }

    // A file the server moved, moved here too - when this device's copy sat at
    // the same place below its own folder that the server's did below its.
    // That is the case for anything this device downloaded and for a library
    // that mirrors the server's layout, and it is the only case in which
    // "the same move" means anything: a copy this device has always filed
    // somewhere else stays where its owner put it.
    //
    // The folder is found from the path itself - whatever is left of it once
    // the server's old relative path is taken off the end - so this needs no
    // list of library folders and works the same for a scanned file and for a
    // download, which live in different places.
    private void ApplyMoves(IReadOnlyList<(Track Local, string From, string To)> moves)
    {
        var comparison = OperatingSystem.IsLinux() ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;
        var moved = 0;
        foreach (var (local, from, to) in moves)
        {
            if (local.Path is not { } path)
                continue;

            var suffix = "/" + from;
            var normalized = path.Replace('\\', '/');
            if (!normalized.EndsWith(suffix, comparison))
                continue;

            var root = path[..^from.Length].TrimEnd('/', '\\');
            var target = System.IO.Path.Combine([root, .. to.Split('/')]);
            try
            {
                // Nothing is overwritten. A name already taken here is a file
                // of this device's that the server knows nothing about.
                if (System.IO.File.Exists(target) || !System.IO.File.Exists(path))
                    continue;

                System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(target)!);
                System.IO.File.Move(path, target);
                _library.MoveTrack(local, target);
                LibraryFolders.RemoveEmptyFolders(System.IO.Path.GetDirectoryName(path), root);
                moved++;
            }
            catch (Exception ex) when (ex is System.IO.IOException or UnauthorizedAccessException)
            {
                _logger.LogWarning(ex, "Could not move {Path} to follow the server's copy", LogPath.Short(path));
            }
        }

        if (moved > 0)
            _logger.LogInformation("Moved {Count} file(s) on this device to where the server now keeps them", moved);
    }

    // New versions this device still has to fetch, per server: found by a
    // merge, and either fetched then or held here until the device is on the
    // server's network. Kept across syncs because the merge that found them
    // will not run again until the catalog next changes - an unchanged catalog
    // is a 304, and a file that was waiting for home would wait for ever.
    private readonly ConcurrentDictionary<string, List<(Track Local, Track Remote)>> _filesOwed = new();

    // A song's file was replaced on the server by a new version, and this
    // device has a copy of the old one: the new one is downloaded and put
    // where the copy was, so that every copy of a song is the same file. A
    // download or a file of this device's own alike - what a device holds of a
    // server's song is a copy either way.
    //
    // A file is megabytes, so this keeps the rule uploads keep: on the
    // server's own network, unless the user has said that files may travel
    // from anywhere (AppSettings.UploadWhenAwayFromHome). Anything held back
    // is fetched by the first sync that finds the device home.
    //
    // Not over a change of this device's own that is still waiting to go up.
    // That copy is itself a newer version, about to replace the server's; the
    // one made last wins, as it does for everything else here.
    private async Task ApplyNewerFilesAsync(DiscoveredDevice device, IReadOnlyList<(Track Local, Track Remote)> found)
    {
        var owed = _filesOwed.GetOrAdd(device.Fingerprint, _ => []);
        List<(Track Local, Track Remote)> pending;
        lock (owed)
        {
            // The newest finding about a song replaces an older one.
            foreach (var item in found)
            {
                owed.RemoveAll(o => o.Local.Id == item.Local.Id);
                owed.Add(item);
            }

            pending = [.. owed];
        }

        if (pending.Count == 0)
            return;

        if (!LibraryMirrorService.IsOnLocalNetwork(device) && !_appSettings.UploadWhenAwayFromHome)
        {
            _logger.LogDebug("{Count} song(s) have a new version on {Alias}; waiting for its network to fetch them", pending.Count, device.Alias);
            return;
        }

        var client = PeerMediaClientFactory.Create(device, _deviceIdentity, _appSettings, _signingKey);
        var importer = new Flower.Importer.Importer(_scanLogger);
        var replaced = 0;
        foreach (var (local, remote) in pending)
        {
            var settled = true;
            try
            {
                settled = await ReplaceWithNewerFileAsync(device, client, importer, local, remote);
                if (settled)
                    replaced++;
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or System.IO.IOException or UnauthorizedAccessException)
            {
                // Still owed. The part that arrived is kept, and the next
                // attempt picks up from it (PeerMediaClient).
                settled = false;
                _logger.LogWarning(ex, "Could not fetch the new version of {Title} from {Alias}; will try again", local.Title, device.Alias);
            }

            if (settled)
            {
                lock (owed)
                    owed.RemoveAll(o => o.Local.Id == local.Id);
            }
        }

        if (replaced > 0)
            _logger.LogInformation("Replaced {Count} file(s) on this device with the newer versions on {Alias}", replaced, device.Alias);
    }

    // True when there is nothing more to do about this song - replaced, or no
    // longer something to replace.
    private async Task<bool> ReplaceWithNewerFileAsync(
        DiscoveredDevice device, PeerMediaClient client, Flower.Importer.Importer importer, Track local, Track remote)
    {
        var current = _library.Tracks.FirstOrDefault(t => t.Id == local.Id);
        if (current?.Path is not { } path || remote.OriginTrackId is not { } id || remote.FileReplacedAt is not { } replacedAt
            || current.OriginTrackId != id)
        {
            return true;
        }

        if (current.FileReplacedAt is { } have && have >= replacedAt)
            return true;

        // This device's own change to the file, not yet sent: see above.
        if (device.WeAreAdmin && !current.IsLocallyDownloaded
            && current.OriginFileStamp is { } sent && LibraryMirrorService.StampOf(path) != sent)
        {
            return true;
        }

        // Named for the version, so a part left over from fetching an older
        // one is never resumed into this one.
        var incoming = $"{path}.{replacedAt.UtcTicks}.incoming";
        await client.DownloadTrackAsync(id, incoming);
        System.IO.File.Move(incoming, path, overwrite: true);

        // Read back the way a scan would, because that is what it now is: a
        // different file at a path the library already knows. Everything the
        // library knew about the song carries over; its tags, its length and
        // its format are the new file's.
        if (importer.ImportFile(path) is not { } scanned)
        {
            _logger.LogWarning("The new version of {Path} from {Alias} could not be read", LogPath.Short(path), device.Alias);
            return false;
        }

        t_mergingPulledCatalog = true;
        try
        {
            _library.AddScannedTrack(
                scanned, tagsEditedAt: remote.TagsEditedAt, artEditedAt: remote.ArtEditedAt,
                fileReplacedAt: replacedAt, fileStamp: LibraryMirrorService.StampOf(path));
        }
        finally
        {
            t_mergingPulledCatalog = false;
        }

        AlbumArtLoader.Invalidate(scanned);
        return true;
    }

    private const string CoverArtPath = "/api/flower/v1/cover-art";

    // Artwork somebody changed on another device, fetched from the server and
    // put into this device's own copies of those songs. The file first, as for
    // tags: a song told about a picture its file never got would be offered it
    // by no later pull.
    private async Task ApplyNewerArtAsync(DiscoveredDevice device, IReadOnlyList<(Track Local, Track Remote)> newer)
    {
        if (newer.Count == 0)
            return;

        var applied = new List<(Track Track, DateTimeOffset EditedAt, string? FileStamp)>();
        foreach (var (local, remote) in newer)
        {
            if (local.Path is not { } path || remote.ArtEditedAt is not { } editedAt || remote.OriginTrackId is not { } id)
                continue;

            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, device.Url($"{CoverArtPath}?id={Uri.EscapeDataString(id)}"));
                await request.AddPeerCredentialsAsync(_credentials, []);
                using var response = await Http.SendAsync(request);

                // No picture there is an answer too: it was removed.
                bool written;
                if (response.StatusCode == HttpStatusCode.NotFound)
                {
                    written = AlbumArtWriter.TryRemove(path, _logger);
                }
                else
                {
                    response.EnsureSuccessStatusCode();
                    var bytes = await response.Content.ReadAsByteArrayAsync();
                    var mimeType = LocalAlbumArtReader.MimeTypeForBytes(bytes)
                                   ?? response.Content.Headers.ContentType?.MediaType ?? "image/jpeg";
                    written = TrackArtwork.Same(LocalAlbumArtReader.EmbeddedIn(path), new LocalAlbumArt(bytes, mimeType))
                              || AlbumArtWriter.TryWrite(path, bytes, mimeType, _logger);
                }

                if (!written)
                    continue;

                applied.Add((local, editedAt, LibraryMirrorService.StampOf(path)));
                AlbumArtLoader.Invalidate(local);
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or System.IO.IOException)
            {
                // Left as it was, and offered again by the next pull.
                _logger.LogDebug(ex, "Could not fetch newer artwork for {Path}", LogPath.Short(path));
            }
        }

        _library.ApplySyncedArt(applied);
        if (applied.Count > 0)
            _logger.LogInformation("Applied artwork changed on another device to {Count} song(s) on this one", applied.Count);
    }

    // Tags somebody edited on another device, written into this device's own
    // copies of those songs - the last leg of an edit's journey: device,
    // server, every other device.
    //
    // The file first. Its tags are what the next scan reads, so a library
    // told about an edit its file never got would lose the edit at the next
    // launch and - having recorded it as received - never be offered it
    // again. A file that cannot be written is therefore left entirely alone,
    // and is offered the same tags by the next pull.
    private void ApplyNewerTags(IReadOnlyList<(Track Local, Track Remote)> newer)
    {
        if (newer.Count == 0)
            return;

        var applied = new List<(Track Track, TrackTagsDto Tags, DateTimeOffset EditedAt, string? FileStamp)>();
        foreach (var (local, remote) in newer)
        {
            if (local.Path is not { } path || remote.TagsEditedAt is not { } editedAt)
                continue;

            var tags = TrackTags.Of(remote);
            try
            {
                // Already what the file says - this device made the edit, or
                // both made the same one. Only the date is news.
                if (TrackTags.Of(local) != tags)
                    TrackTags.WriteToFile(path, tags);

                // What the file looks like now. Writing tags into it changed
                // it, and it has not thereby become something to upload.
                applied.Add((local, tags, editedAt, LibraryMirrorService.StampOf(path)));
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Could not write the server's newer tags into {Path}; will try again on the next sync", LogPath.Short(path));
            }
        }

        _library.ApplySyncedTags(applied);
        if (applied.Count > 0)
            _logger.LogInformation("Applied tags edited on another device to {Count} song(s) on this one", applied.Count);
    }

    private void DeleteLentCopies(IReadOnlyList<string> paths)
    {
        var deleted = new List<string>();
        foreach (var path in paths)
        {
            // Only what the merge actually set aside: a download the merge
            // kept, for whatever reason, is not this method's to second-guess.
            if (!_library.IsExcludedPath(path))
                continue;

            try
            {
                System.IO.File.Delete(path);
                deleted.Add(path);
            }
            catch (Exception ex) when (ex is System.IO.IOException or UnauthorizedAccessException)
            {
                // Stays set aside, which is still out of the library.
                _logger.LogDebug(ex, "Could not delete the downloaded copy at {Path} of a song the server removed", LogPath.Short(path));
            }
        }

        _library.ForgetExcludedPaths(deleted);
        if (deleted.Count > 0)
            _logger.LogInformation("Deleted {Count} downloaded copy(ies) of songs the server removed", deleted.Count);
    }

    // What this device knows about the server's tracks that the server does
    // not - the client half of POST /track-state.
    //
    // Not gated on a setting the way the log push is. A log snapshot carries
    // exception text and absolute paths off the device, which is a disclosure
    // to opt into; a play count or a star is the shared library working as
    // intended, and it is already the same number the pairing showed the user
    // they were joining.
    //
    // Nothing here distinguishes a downloaded track from a placeholder, or a
    // file this device imported itself that the server happens to also have.
    // If it carries the server's own id for the track (see
    // Track.OriginTrackId, which MergeSyncedTracks stamps on a local match
    // too), a play of it is a play of that song in that shared library, and
    // the server files it under this device's fingerprint rather than adding
    // it to its own - so counting it in both places is not double counting.
    //
    // Virtual for the same reason the two methods above are: it is the seam a
    // coordinator test drives without standing up a server.
    public virtual async Task<bool> PushTrackStateAsync(DiscoveredDevice device)
    {
        if (string.IsNullOrEmpty(device.Fingerprint))
            return true;

        // Only the tracks marked since the last push that got through, or every
        // track when something says to restate the lot. Nothing marked is
        // nothing to do - not a pass over the whole library that finds nothing,
        // which is what every five-second tick used to cost.
        if (TakeTrackStateCandidates() is not { } candidates)
            return true;

        try
        {
            var sentCounts = _sentCounts.GetOrAdd(device.Fingerprint, _ => new ConcurrentDictionary<string, int>());
            var known = _knownServerState.GetOrAdd(device.Fingerprint, _ => new ConcurrentDictionary<string, TrackStateSnapshot>());

            // Only for a server that made this device an admin. The server
            // enforces this too and is the side that has to (see
            // SyncEndpoints.ReportTrackState) - this is the same claim read
            // from the /info answer, kept here so a phone that is only a
            // listener does not spend a request stating what will be dropped.
            var reportOwnerState = device.WeAreAdmin;
            var report = UnreportedTrackState(candidates.Tracks, sentCounts, known, reportOwnerState);

            if (report.Count == 0)
                return true;

            var bodyBytes = JsonSerializer.SerializeToUtf8Bytes(
                new TrackStateReportDto(report), PlayReportJsonContext.Default.TrackStateReportDto);

            using var request = new HttpRequestMessage(HttpMethod.Post, device.Url(TrackStateReportPath));
            await request.AddPeerCredentialsAsync(_credentials, bodyBytes);
            request.Headers.ConnectionClose = true;
            using var content = new ByteArrayContent(bodyBytes);
            content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/json");
            request.Content = content;

            using var response = await Http.SendAsync(request);
            response.EnsureSuccessStatusCode();

            // See TrackStateReportHeaders: this is the token our own report
            // produced, remembered so the /info poll can tell that change apart
            // from one worth pulling a whole catalog for.
            if (response.Headers.TryGetValues(TrackStateReportHeaders.LibraryToken, out var echoedTokens))
            {
                foreach (var echoed in echoedTokens)
                {
                    if (!string.IsNullOrEmpty(echoed))
                        _tokensThisDeviceCaused[device.Fingerprint] = echoed;
                }
            }

            // Only on a 2xx. A failed push leaves the marks where they were, so
            // the same values go out again with the next tick rather than being
            // silently dropped - and because they are values rather than
            // changes, the retry needs no backlog of its own to carry.
            foreach (var entry in report)
            {
                sentCounts[entry.TrackId] = entry.Count;
                if (reportOwnerState)
                    known[entry.TrackId] = TrackStateSnapshot.Of(entry);
            }

            // The server took these stars as stated, so they are what the two
            // sides now agree on - the baseline the next pull's merge needs.
            // Left for that pull to record instead, a star unstarred here and
            // restarred elsewhere before it would read as this device's own
            // change and be reported back over the other one.
            if (reportOwnerState)
                await _starBaselines.ApplyAsync(device.Fingerprint, report.Select(e => (e.TrackId, e.Starred)));

            _logger.LogDebug("Reported {Count} track state(s) to {Alias}", report.Count, device.Alias);
            return true;
        }
        catch (Exception ex)
        {
            // Debug, not Warning: this runs on the same five-second tick the
            // log push does, against a server that is very often simply not
            // there, and nothing is lost - it is all still in the library, and
            // still the truth to be stated next time.
            _logger.LogDebug(ex, "Could not report track state to {Alias} ({Fingerprint})", device.Alias, device.Fingerprint);
            ReturnTrackStateCandidates(candidates);
            return false;
        }
    }

    // The log half of a sync on its own, without the catalog pull above.
    //
    // PeerSyncCoordinator's periodic tick wants exactly this and nothing else:
    // new log lines appear at roughly the same cadence as its timer, so a tick
    // that ran a whole SyncWithAsync spent a GET /library (plus its playlist
    // twin) every five seconds to deliver a payload that is usually a handful
    // of lines - four bulk-group requests a tick against a budget of sixty a
    // minute, which the server answers with 429s that are themselves logged,
    // which arms the next tick. The catalog has its own trigger for the only
    // thing that should move it: an actual local change (ScheduleContentSync).
    //
    // Virtual for the same reason SyncWithAsync is - it is the seam a test
    // drives the coordinator's tick through.
    public virtual Task<bool> PushLogsOnlyAsync(DiscoveredDevice device)
    {
        // Same gate the tail of SyncWithAsync applies, restated rather than
        // shared because this is a second public door into the same push: the
        // setting ships off by default and a snapshot carries exception text
        // and absolute paths, so neither door may open without it.
        if (!_appSettings.ShareLogsWithPairedServer || string.IsNullOrEmpty(device.Fingerprint))
            return Task.FromResult(true);

        // Still pending, so reported as not done - but not attempted either.
        if (_logPushParkedAt.TryGetValue(device.Fingerprint, out var parkedAt) && _logArchive.LiveSequence <= parkedAt)
            return Task.FromResult(false);

        return PushLogSnapshotAsync(device);
    }

    // The owner-state fields of one track, as one comparable value: what this
    // device would tell a server, and what a server last told this device, in
    // the same shape so that "has this changed" is `!=` rather than six
    // hand-written comparisons that drift apart.
    //
    // StarredAt is deliberately not in it. It is derived from Starred - set on
    // starring, nulled on unstarring - so including it would make two devices
    // that agree on the star look like they disagree, forever, over the second
    // it was clicked at.
    internal sealed record TrackStateSnapshot(
        DateTimeOffset? LastPlayedAt,
        bool Starred,
        bool RememberPlaybackPosition,
        TimeSpan? ResumePosition,
        bool IgnoreWhenShuffling,
        int VolumeAdjustment,
        DateTimeOffset? DateAdded = null)
    {
        public static TrackStateSnapshot Of(Track track) => new(
            track.LastPlayedAt, track.Starred, track.RememberPlaybackPosition,
            track.ResumePosition, track.IgnoreWhenShuffling, track.VolumeAdjustment, track.DateAdded);

        public static TrackStateSnapshot Of(TrackStateDto entry) => new(
            entry.LastPlayedAt, entry.Starred, entry.RememberPlaybackPosition,
            entry.ResumePositionSeconds is { } seconds ? TimeSpan.FromSeconds(seconds) : null,
            entry.IgnoreWhenShuffling, entry.VolumeAdjustment, entry.DateAdded);

        // Would telling the server this actually move it? Deliberately not
        // `!=` against what it served: the two sides have to agree about what
        // counts as news, and Library.ApplyReportedOwnerState is the side that
        // decides. A plain difference test reported everything the server
        // holds and this device does not - a track played on the desktop and
        // never on the phone reads as a difference forever - and each of those
        // reports was refused, re-seeded by the next catalog pull (see
        // SeedKnownServerState) and sent again, which is what the server's
        // "Applied 0 of 11 track state report(s)" line was, once per pull,
        // indefinitely.
        public bool WouldMove(TrackStateSnapshot served) =>
            Starred != served.Starred
            || RememberPlaybackPosition != served.RememberPlaybackPosition
            || IgnoreWhenShuffling != served.IgnoreWhenShuffling
            || VolumeAdjustment != served.VolumeAdjustment
            || MovesListeningForward(served)
            || MovesPositionWithinTheSameListen(served)
            || MovesDateAddedBack(served);

        // The mirror image of the listening rule below: Date Added is a
        // low-water mark there, so only an older one is news. A device that
        // met the song later than the server did has nothing to add.
        private bool MovesDateAddedBack(TrackStateSnapshot served) =>
            DateAdded is { } mine && served.DateAdded is { } theirs && mine < theirs;

        // LastPlayedAt is a high-water mark there, so only a later one is
        // news - and a device that has never played the track has no opinion
        // at all rather than an early one. A later listen carries its resume
        // position with it.
        private bool MovesListeningForward(TrackStateSnapshot served) =>
            LastPlayedAt is { } mine && (served.LastPlayedAt is not { } theirs || mine > theirs);

        // Where a sitting the server already knows about has since got to - a
        // pause, a track change, the app going away. Only for that same
        // sitting: where an older one got to is refused (see
        // ApplyReportedOwnerState), since a position only means something
        // attached to its own listen. Whole seconds, because that is what a
        // catalog serves it in (LibraryDtoMapper), and a finer difference would
        // read as news after every pull and go out again each time.
        private bool MovesPositionWithinTheSameListen(TrackStateSnapshot served) =>
            LastPlayedAt is { } mine && served.LastPlayedAt == mine
            && WholeSeconds(ResumePosition) != WholeSeconds(served.ResumePosition);

        private static int? WholeSeconds(TimeSpan? position) =>
            position is { } p ? (int)p.TotalSeconds : null;
    }

    // What this device would say about the server's tracks that the server has
    // not already been told.
    //
    // Static and pure so the selection rule is testable without a server: what
    // counts as this device's own, and what is already known there, is the
    // whole of the decision - the rest of the push is transport.
    //
    // The two halves answer "already known there?" differently, because the
    // two halves converge differently.
    //
    // A count is a G-Counter: it only grows and the far side takes the max, so
    // the baseline is simply the highest this device has successfully sent,
    // and re-sending is harmless. It needs no seed - a restart re-states every
    // total once and nothing is wrong for having said the same true thing
    // twice.
    //
    // The owner state has no such property. Starred in particular is a toggle
    // the server applies as stated (see Library.ApplyReportedOwnerState),
    // which is only safe because of the baseline used here: what the *server*
    // last said, seeded from the catalog pull. Compare against that and this
    // device speaks up exactly when its answer differs from the server's -
    // which for an unchanged track is never, so a restart re-states nothing
    // and cannot walk back a star some other client set in the meantime -
    // narrowed by TrackStateSnapshot.WouldMove to the differences the server
    // would actually act on, since the rest are offered again after every
    // pull and refused every time. A
    // track with no seed at all is a track this session has not pulled yet,
    // and it is left alone for the same reason: with nothing to compare
    // against there is no way to tell a local change from a local value.
    internal static List<TrackStateDto> UnreportedTrackState(
        IEnumerable<Track> tracks,
        IReadOnlyDictionary<string, int> sentCounts,
        IReadOnlyDictionary<string, TrackStateSnapshot> knownServerState,
        bool includeOwnerState)
    {
        var report = new List<TrackStateDto>();
        foreach (var track in tracks)
        {
            // No id the server knows this track by - a file of this device's
            // own that the server does not have. Not its play to count, and
            // not its copy to star.
            if (track.OriginTrackId is not { Length: > 0 } originTrackId)
                continue;

            // The same sum LibraryDtoMapper sends as
            // this device's own tally - a play imported from iTunes is still a
            // play this device is the record of.
            var total = track.PlayCount + track.ImportedPlayCount;
            var countIsNews = total > 0 && sentCounts.GetValueOrDefault(originTrackId) < total;

            var local = TrackStateSnapshot.Of(track);
            var stateIsNews = includeOwnerState
                && knownServerState.TryGetValue(originTrackId, out var known)
                && local.WouldMove(known);

            if (!countIsNews && !stateIsNews)
                continue;

            // The count rides along on a state-only report and vice versa,
            // rather than being conditionally omitted. Both are values, so
            // restating one costs the far side a comparison that finds nothing
            // - and a report that carried only the half that moved would need
            // a way to say "no opinion" about the other, which for a bool is a
            // third state this wire format would then have to grow.
            report.Add(includeOwnerState
                ? new TrackStateDto(
                    originTrackId, total,
                    track.LastPlayedAt, track.Starred, track.StarredAt,
                    track.RememberPlaybackPosition, track.ResumePosition?.TotalSeconds,
                    track.IgnoreWhenShuffling, track.VolumeAdjustment, track.DateAdded)
                : new TrackStateDto(originTrackId, total));
        }

        return report;
    }

    // What the server itself last said about each of its tracks, recorded from
    // the catalog this device just pulled. Not applied to the local tracks -
    // MergeSyncedTracks decides what a pull is allowed to overwrite, and this
    // deliberately does not widen it - only remembered, so the push above can
    // tell a local change from a local value.
    private void SeedKnownServerState(string peerFingerprint, IReadOnlyList<Track> served)
    {
        var known = _knownServerState.GetOrAdd(peerFingerprint, _ => new ConcurrentDictionary<string, TrackStateSnapshot>());
        var tagEdits = new ConcurrentDictionary<string, DateTimeOffset>();
        var artEdits = new ConcurrentDictionary<string, DateTimeOffset>();
        foreach (var track in served)
        {
            if (track.OriginTrackId is not { Length: > 0 } originTrackId)
                continue;

            known[originTrackId] = TrackStateSnapshot.Of(track);
            if (track.TagsEditedAt is { } editedAt)
                tagEdits[originTrackId] = editedAt;
            if (track.ArtEditedAt is { } repaintedAt)
                artEdits[originTrackId] = repaintedAt;
        }

        // Replaced whole, unlike the state above: a song absent from here is
        // one the server has no edit for, and that is an answer too.
        _knownServerTagEdits[peerFingerprint] = tagEdits;
        _knownServerArtEdits[peerFingerprint] = artEdits;
    }

    // The same for artwork.
    private readonly ConcurrentDictionary<string, ConcurrentDictionary<string, DateTimeOffset>> _knownServerArtEdits = new();

    public virtual DateTimeOffset? ServerArtEditedAt(string fingerprint, string trackId) =>
        _knownServerArtEdits.TryGetValue(fingerprint, out var edits) && edits.TryGetValue(trackId, out var at) ? at : null;

    public virtual void NoteServerArtEditedAt(string fingerprint, string trackId, DateTimeOffset editedAt) =>
        _knownServerArtEdits.GetOrAdd(fingerprint, _ => new ConcurrentDictionary<string, DateTimeOffset>())[trackId] = editedAt;

    // How new the tags are that a server holds for each of its songs, as of
    // the last catalog it served and whatever this device has sent it since.
    // What LibraryMirrorService compares a local edit against, to tell one
    // still owed to the server from one the server already has.
    private readonly ConcurrentDictionary<string, ConcurrentDictionary<string, DateTimeOffset>> _knownServerTagEdits = new();

    public virtual DateTimeOffset? ServerTagsEditedAt(string fingerprint, string trackId) =>
        _knownServerTagEdits.TryGetValue(fingerprint, out var edits) && edits.TryGetValue(trackId, out var at) ? at : null;

    public virtual void NoteServerTagsEditedAt(string fingerprint, string trackId, DateTimeOffset editedAt) =>
        _knownServerTagEdits.GetOrAdd(fingerprint, _ => new ConcurrentDictionary<string, DateTimeOffset>())[trackId] = editedAt;

    // Per-peer, in-memory, and per-track: the highest total this device has
    // successfully told that peer, and the last thing that peer said about the
    // rest. Not persisted, for the same reason _lastSeenTokens is not - a
    // restart re-pulls, which re-seeds the second of these, and the first
    // re-sends into a max-merge.
    private readonly ConcurrentDictionary<string, ConcurrentDictionary<string, int>> _sentCounts = new();
    private readonly ConcurrentDictionary<string, ConcurrentDictionary<string, TrackStateSnapshot>> _knownServerState = new();

    // What PushTrackStateAsync has to look at next: the tracks marked since the
    // last push that got through, or all of them. Starts at all of them - a
    // launch restates every total once, which a max-merge makes harmless - and
    // goes back to all of them whenever the library itself changes, since a
    // rescan replaces every Track and a pull re-seeds the baseline.
    private readonly ConcurrentDictionary<Guid, Track> _unreportedTracks = new();
    private int _restateAllTrackState = 1;

    internal sealed record TrackStateCandidates(IReadOnlyList<Track> Tracks, bool Everything, IReadOnlyList<Track> Marked);

    // A play, a star, an option or a resume position on these tracks - see
    // PeerSyncCoordinator's Library.TrackChanged handler.
    public void MarkTrackStateChanged(IEnumerable<Track> tracks)
    {
        foreach (var track in tracks)
            _unreportedTracks[track.Id] = track;
    }

    public void MarkAllTrackStateChanged() => Interlocked.Exchange(ref _restateAllTrackState, 1);

    // Takes whatever is pending, leaving nothing behind for a concurrent push
    // to send twice; null when nothing is. A push that fails hands it back.
    internal TrackStateCandidates? TakeTrackStateCandidates()
    {
        var everything = Interlocked.Exchange(ref _restateAllTrackState, 0) == 1;
        var marked = new List<Track>();
        foreach (var id in _unreportedTracks.Keys)
        {
            if (_unreportedTracks.TryRemove(id, out var track))
                marked.Add(track);
        }

        if (!everything && marked.Count == 0)
            return null;

        return new TrackStateCandidates(everything ? _library.Tracks : marked, everything, marked);
    }

    // TryAdd, so a newer mark made while the push was out is not replaced.
    internal void ReturnTrackStateCandidates(TrackStateCandidates candidates)
    {
        if (candidates.Everything)
            MarkAllTrackStateChanged();
        foreach (var track in candidates.Marked)
            _unreportedTracks.TryAdd(track.Id, track);
    }

    private const string TrackStateReportPath = "/api/flower/v1/track-state";

    private const string LogReportPath = "/api/flower/v1/log/report";
    private const string LogWatermarkPath = "/api/flower/v1/log/watermark";

    // Where each peer's copy of this device's log ends, as that peer reported
    // it: the (Timestamp, EventId) of the newest line it holds. A push sends
    // everything the archive has after that point, so a server that has been
    // down for a day gets the day it missed rather than the 2000-line memory
    // ring that is all a client used to be able to offer.
    //
    // Cached per peer, in memory. The first push of a session asks the server
    // outright (GET /log/watermark) rather than assuming, which is the whole
    // point of asking: a restarted client has no idea what landed, and a
    // restored-from-backup server may hold less than it did.
    private readonly ConcurrentDictionary<string, LogWatermarkDto> _logWatermarks = new();

    // Peers whose last log push failed, so a server that is simply down logs
    // one warning instead of one every five seconds - the same first-miss-loud,
    // repeats-quiet shape NetworkDiscoveryService.HandleUnreachable uses, and
    // for the same reason: these lines land in the very archive being pushed,
    // so a chatty failure path floods out the content it exists to deliver.
    private readonly ConcurrentDictionary<string, int> _logPushFailures = new();

    // Where the live ring stood just after a peer's push failed and said so.
    // The next push waits until something has been logged past it: retrying a
    // down server every tick meant a device with nothing to say kept sending,
    // each time on the strength of the line its own last failure had written.
    // Whatever gets logged next - discovery noticing the server come back,
    // most likely - is what lets the pending lines go out, with it.
    private readonly ConcurrentDictionary<string, long> _logPushParkedAt = new();

    // Move everything newly logged out of the memory ring and onto disk, where
    // it survives the restart and the week. Runs on its own tick rather than as
    // a step of a push, because the lines most worth keeping are the ones
    // logged while no server was reachable to push to.
    public Task ArchiveOwnLogsAsync() =>
        Task.Run(() => _logArchive.Ingest(_deviceIdentity.Fingerprint, _deviceIdentity.Alias));

    // One report's worth, and how many reports one session sends. The server
    // takes at most ClientLogStore.MaxEntriesPerReport lines a report and
    // refuses a body past its cap for this route (SyncEndpoints), so a backlog
    // goes in pieces: a week of a phone's log is a few hundred thousand lines,
    // which as one request was larger than the server would ever accept and
    // failed on every session for good. The session limit keeps a long
    // backlog from spending the sync budget the rest of the session needs;
    // the next session carries on from wherever the server's watermark says.
    private const int MaxLogReportBytes = 2 * 1024 * 1024;
    private const int MaxLogReportsPerSession = 10;

    private async Task<bool> PushLogSnapshotAsync(DiscoveredDevice device)
    {
        try
        {
            if (!_logWatermarks.TryGetValue(device.Fingerprint, out var watermark))
            {
                watermark = await FetchLogWatermarkAsync(device);
                _logWatermarks[device.Fingerprint] = watermark;
            }

            for (var round = 0; round < MaxLogReportsPerSession; round++)
            {
                var entries = NextLogReport(_logArchive.EntriesAfter(watermark));

                // Nothing this peer is missing. Reported as success: "delivered
                // everything there is" is exactly the state the caller's retry
                // logic should treat as settled.
                if (entries.Count == 0)
                    break;

                var report = new LogReportDto(_deviceIdentity.Fingerprint, _deviceIdentity.Alias, DateTimeOffset.UtcNow, entries);
                var bodyBytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(report, FlowerJsonContext.Default.LogReportDto));

                using var request = new HttpRequestMessage(HttpMethod.Post, device.Url(LogReportPath));
                await request.AddPeerCredentialsAsync(_credentials, bodyBytes);
                request.Headers.ConnectionClose = true;
                using var content = new ByteArrayContent(bodyBytes);
                content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/json");
                request.Content = content;

                using var response = await Http.SendAsync(request);
                response.EnsureSuccessStatusCode();

                // Only now, and only on a 2xx. The server's own answer is preferred
                // over what was sent, because the two can legitimately differ: it
                // drops anything already past its retention window, and saying so
                // stops the client waiting for a gap that will never be filled.
                // Falling back to what was sent keeps an older server - or an empty
                // body - from resetting the mark and replaying the week.
                var next = await ReadWatermarkAsync(response) ?? DeviceLogArchive.WatermarkOf(entries);
                _logWatermarks[device.Fingerprint] = next;

                // A server that kept none of it - one holding all it will hold
                // for this device - leaves the mark where it was. Asking again
                // this session would only be told the same.
                if (next == watermark)
                    break;
                watermark = next;
            }

            ClearLogPushFailures(device);
            return true;
        }
        catch (Exception ex)
        {
            // Not fatal to the library sync itself - the library merge above
            // already succeeded and saved; the mark is left where it was, so
            // these lines go out again on the next cycle. Nothing is lost
            // either way: the archive holds a week regardless of whether any of
            // it has been delivered.
            //
            // The first failure is a Warning rather than the Debug this used to
            // be at every level: the only route this line has off the device is
            // the push that just failed, so it has to be loud enough to survive
            // in the archive until the server comes back and reads it - and it
            // has to name the address actually dialled, which is the one thing
            // that distinguishes "the server is down" from "we are pushing at
            // the wrong endpoint".
            var failures = _logPushFailures.AddOrUpdate(device.Fingerprint, 1, (_, count) => count + 1);
            if (failures == 1)
                _logger.LogWarning(ex, "Could not push log lines to {Alias} at {Endpoint} - not fatal to this sync, will retry",
                    device.Alias, device.Url(LogReportPath));
            else
                _logger.LogDebug(ex, "Log push to {Alias} still failing ({Failures} consecutive)", device.Alias, failures);
            _logPushParkedAt[device.Fingerprint] = _logArchive.LiveSequence;
            return false;
        }
    }

    // The oldest of what is pending, as much as one report carries - by line
    // count, and by an estimate of size that is generous rather than exact,
    // since serializing each line to measure it would cost more than the push.
    // Always at least one line, so a single enormous one still goes.
    private static List<LogEntryDto> NextLogReport(IReadOnlyList<LogEntryDto> pending)
    {
        var report = new List<LogEntryDto>();
        long bytes = 0;
        foreach (var entry in pending)
        {
            bytes += 256 + 2L * ((entry.Message?.Length ?? 0) + (entry.Exception?.Length ?? 0) + (entry.SourceContext?.Length ?? 0));
            if (report.Count > 0 && (report.Count >= ClientLogStore.MaxEntriesPerReport || bytes > MaxLogReportBytes))
                break;

            report.Add(entry);
        }

        return report;
    }

    private void ClearLogPushFailures(DiscoveredDevice device)
    {
        _logPushParkedAt.TryRemove(device.Fingerprint, out _);
        if (_logPushFailures.TryRemove(device.Fingerprint, out var failures))
            _logger.LogInformation("Log push to {Alias} recovered after {Failures} failed attempt(s)", device.Alias, failures);
    }

    private async Task<LogWatermarkDto> FetchLogWatermarkAsync(DiscoveredDevice device)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, device.Url(LogWatermarkPath));
        await request.AddPeerCredentialsAsync(_credentials, []);
        request.Headers.ConnectionClose = true;

        using var response = await Http.SendAsync(request);
        response.EnsureSuccessStatusCode();

        // An empty or unreadable answer is read as "nothing stored", which
        // costs one oversized first push that the server's event hashes
        // deduplicate - the safe direction to be wrong in.
        return await ReadWatermarkAsync(response) ?? new LogWatermarkDto(null, null);
    }

    private static async Task<LogWatermarkDto?> ReadWatermarkAsync(HttpResponseMessage response)
    {
        try
        {
            var json = await response.Content.ReadAsStringAsync();
            return string.IsNullOrWhiteSpace(json)
                ? null
                : JsonSerializer.Deserialize(json, FlowerJsonContext.Default.LogWatermarkDto);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    // An HttpRequestException carrying no StatusCode never reached a server at
    // all - the transport failed, which is the only case that means what the
    // one old message said. One that carries a status *was* answered, and the
    // status is the answer: 429 is a peer that is right there and busy, 403 is
    // one that is right there and refusing us. A cancellation is a timeout
    // rather than a user gesture, because nothing passes a token into this path.
    private static SyncFailure Classify(Exception ex) => ex switch
    {
        HttpRequestException { StatusCode: HttpStatusCode.TooManyRequests } => SyncFailure.Throttled,
        PeerRefusedException { Code: ProblemCodes.DeviceUnknown, SignedByServer: true } => SyncFailure.NotTrusted,
        HttpRequestException { StatusCode: not null } => SyncFailure.Refused,
        HttpRequestException => SyncFailure.Unreachable,
        TaskCanceledException => SyncFailure.Unreachable,
        _ => SyncFailure.Refused,
    };
}
