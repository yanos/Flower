using System;
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
public readonly record struct LibrarySyncResult(bool Success, int FetchedCount, int AddedCount, bool Unchanged = false);

// Pulls a peer's full track catalog in one request (GET /api/flower/v1/library
// - see LibrarySyncContracts) and merges anything this device doesn't already
// have as Path == null placeholders - see SYNC-PLAN.md Phase 3. Talks to the
// server's bulk endpoint directly (same signed identity headers as
// PlaylistSyncService, not real OpenSubsonic credentials - see Flower.Server's
// SyncEndpoints) rather than through OpenSubsonicClient: an
// earlier version used the OpenSubsonic-shaped getAlbumList2/getAlbum pair,
// one request per album, which for a library of hundreds/thousands of albums
// meant hundreds/thousands of individual connections in a burst - observed in
// practice as heavy iOS nw_connection log churn. OpenSubsonicClient itself is
// unaffected and still used for the OpenSubsonic-shaped endpoints (stream/
// download, and real third-party server support later).
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
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromMinutes(2) };

    // The ETag (Library.ChangeToken) each peer served with the manifest we
    // last successfully merged from it, sent back as If-None-Match so an
    // unchanged catalog costs one 304 instead of 6-8 MB - see
    // SyncEndpoints' GET /library, ARCHITECTURE-REVIEW Tier 1.4.
    // In-memory only: the token is session-scoped on the serving side anyway
    // (see Library.ChangeToken), so persisting it would buy nothing.
    private readonly ConcurrentDictionary<string, string> _lastSeenTokens = new();

    private readonly Library _library;
    private readonly DeviceIdentity _deviceIdentity;
    private readonly IPeerCredentials _credentials;
    private readonly AppSettings _appSettings;
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

    public LibrarySyncService(Library library, DeviceIdentity deviceIdentity, DeviceSigningKey signingKey, AppSettings appSettings, DeviceLogArchive logArchive, ILogger<LibrarySyncService> logger, ILogger<RemoteLibraryImporter> importerLogger)
    {
        _library = library;
        _deviceIdentity = deviceIdentity;
        // Constructed here rather than injected: every caller that could supply
        // one would build it from exactly these three, which the container
        // already hands this service.
        _credentials = new SignedDeviceCredentials(deviceIdentity, signingKey);
        _appSettings = appSettings;
        _logArchive = logArchive;
        _logger = logger;
        _importerLogger = importerLogger;
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
                _importerLogger, closeConnection: true);

            var fetch = await importer.FetchAsync(_lastSeenTokens.GetValueOrDefault(device.Fingerprint));
            if (fetch.NotModified)
            {
                _logger.LogTrace("Library sync with {Alias}: catalog unchanged since {Token}, nothing to merge",
                    device.Alias, fetch.ETag);
                return new LibrarySyncResult(true, 0, 0, Unchanged: true);
            }

            placeholders = fetch.Tracks;
            servedToken = fetch.ETag;
            fetchedCount = placeholders.Count;
        }
        catch (Exception ex)
        {
            // Peer unreachable, not running this endpoint yet, or not (yet) trusted.
            _logger.LogWarning(ex, "Library sync with {Alias} ({Fingerprint}): GET /library failed, aborting this sync attempt",
                device.Alias, device.Fingerprint);

            // See PlaylistSyncService's identical check - a 403 here means the same
            // thing, just from this service's own (also trust-gated) first request.
            if (ex is HttpRequestException { StatusCode: HttpStatusCode.Forbidden })
                PeerTrustRejected?.Invoke(this, new PeerTrustRejectedEventArgs { Fingerprint = device.Fingerprint, Alias = device.Alias });

            return new LibrarySyncResult(false, 0, 0);
        }

        _logger.LogInformation("Library sync with {Alias}: fetched {SongCount} song(s) from their catalog", device.Alias, fetchedCount);

        // No early-return for an empty catalog: a peer reporting zero songs
        // (its whole library emptied, or a fresh pairing to one with nothing
        // yet) must still prune every not-yet-downloaded placeholder this
        // device previously learned from it - see Library.MergeSyncedTracks.
        var beforeCount = _library.Tracks.Count;
        var removedCount = _library.MergeSyncedTracks(device.Fingerprint, placeholders);
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
        await PushTrackStateAsync(device);

        if (_appSettings.ShareLogsWithPairedServer)
            await PushLogSnapshotAsync(device);

        return new LibrarySyncResult(true, fetchedCount, addedCount);
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
            var report = UnreportedTrackState(_library.Tracks, sentCounts, known, reportOwnerState);

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
            return false;
        }
    }

    // The log half of a sync on its own, without the catalog pull above.
    //
    // PeerSyncCoordinator's periodic tick wants exactly this and nothing else:
    // new log lines appear at roughly the same cadence as its timer, so a tick
    // that ran a whole SyncWithAsync spent a GET /library (plus its playlist
    // twin) every five seconds to deliver a payload that is usually a handful
    // of lines - four bulk-group requests a tick against a budget of twenty a
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
        int VolumeAdjustment)
    {
        public static TrackStateSnapshot Of(Track track) => new(
            track.LastPlayedAt, track.Starred, track.RememberPlaybackPosition,
            track.ResumePosition, track.IgnoreWhenShuffling, track.VolumeAdjustment);

        public static TrackStateSnapshot Of(TrackStateDto entry) => new(
            entry.LastPlayedAt, entry.Starred, entry.RememberPlaybackPosition,
            entry.ResumePositionSeconds is { } seconds ? TimeSpan.FromSeconds(seconds) : null,
            entry.IgnoreWhenShuffling, entry.VolumeAdjustment);
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
    // and cannot walk back a star some other client set in the meantime. A
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

            // The same sum SubsonicMapper/LibraryOpenSubsonicMapper send as
            // this device's own tally - a play imported from iTunes is still a
            // play this device is the record of.
            var total = track.PlayCount + track.ImportedPlayCount;
            var countIsNews = total > 0 && sentCounts.GetValueOrDefault(originTrackId) < total;

            var local = TrackStateSnapshot.Of(track);
            var stateIsNews = includeOwnerState
                && knownServerState.TryGetValue(originTrackId, out var known)
                && known != local;

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
                    track.IgnoreWhenShuffling, track.VolumeAdjustment)
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
        foreach (var track in served)
        {
            if (track.OriginTrackId is { Length: > 0 } originTrackId)
                known[originTrackId] = TrackStateSnapshot.Of(track);
        }
    }

    // Per-peer, in-memory, and per-track: the highest total this device has
    // successfully told that peer, and the last thing that peer said about the
    // rest. Not persisted, for the same reason _lastSeenTokens is not - a
    // restart re-pulls, which re-seeds the second of these, and the first
    // re-sends into a max-merge.
    private readonly ConcurrentDictionary<string, ConcurrentDictionary<string, int>> _sentCounts = new();
    private readonly ConcurrentDictionary<string, ConcurrentDictionary<string, TrackStateSnapshot>> _knownServerState = new();

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

    // Move everything newly logged out of the memory ring and onto disk, where
    // it survives the restart and the week. Runs on its own tick rather than as
    // a step of a push, because the lines most worth keeping are the ones
    // logged while no server was reachable to push to.
    public Task ArchiveOwnLogsAsync() =>
        Task.Run(() => _logArchive.Ingest(_deviceIdentity.Fingerprint, _deviceIdentity.Alias));

    private async Task<bool> PushLogSnapshotAsync(DiscoveredDevice device)
    {
        IReadOnlyList<LogEntryDto> entries;
        try
        {
            if (!_logWatermarks.TryGetValue(device.Fingerprint, out var watermark))
            {
                watermark = await FetchLogWatermarkAsync(device);
                _logWatermarks[device.Fingerprint] = watermark;
            }

            entries = _logArchive.EntriesAfter(watermark);

            // Nothing this peer is missing. Reported as success: "delivered
            // everything there is" is exactly the state the caller's retry
            // logic should treat as settled.
            if (entries.Count == 0)
            {
                ClearLogPushFailures(device);
                return true;
            }

            var report = new LogReportDto(_deviceIdentity.Fingerprint, _deviceIdentity.Alias, DateTimeOffset.UtcNow, entries.ToList());
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
            _logWatermarks[device.Fingerprint] =
                await ReadWatermarkAsync(response) ?? DeviceLogArchive.WatermarkOf(entries);

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
            return false;
        }
    }

    private void ClearLogPushFailures(DiscoveredDevice device)
    {
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
}
