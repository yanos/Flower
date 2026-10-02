using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;

using Microsoft.Extensions.Logging;

using Flower.Logging;
using Flower.Models;
using Flower.Persistence;

namespace Flower.Services;

// How a round of uploads went. StoppedBecause is set when it ended early for a
// reason that was not about one file - the server could not be written to, or
// could not be reached - and Remaining is what is still waiting for the next.
//
// Uploaded counts files sent, new songs and new versions of existing ones
// alike; Moved counts songs whose file the server was asked to move.
public sealed record LibraryUploadSummary(int Uploaded, int Refused, int Remaining, string? StoppedBecause = null, int Moved = 0)
{
    public static readonly LibraryUploadSummary Nothing = new(0, 0, 0);
}

// Keeps the paired server's files in step with this device's own - the half of
// sync that moves audio *towards* the server, where everything before it moved
// only metadata up and files down. See docs/SYNC-PLAN.md: the library lives on
// the server, devices hold subsets of it, and a song that exists on one device
// only is a song the other devices cannot play.
//
// Four things, and only for a device the server made an admin - changing what
// files a server has is an owner's act, and the server refuses all of it from
// anyone else (these are /api/admin routes):
//
//   - A file of this device's own that the server does not have is uploaded,
//     to the same place below the server's music folder that it has below this
//     device's. See LibraryIngest for the receiving end.
//   - A file that has changed here since the server got it - its tags edited,
//     its artwork replaced - is sent again and replaces the server's copy.
//     Tags live in the file, so this is how an edit travels.
//   - A file that has been moved or renamed here is moved on the server.
//   - A file that has gone from this device's disk is removed from the
//     server's library. Removed, not deleted: the server keeps the file, out
//     of its library and out of its scans, until its owner cleans up
//     (Settings' Removed Songs). Every other device then drops the song too.
//
// The files themselves only travel on the server's own network, unless the
// user has said otherwise (AppSettings.UploadWhenAwayFromHome) - an album is a
// lot of somebody's data plan. The news that a song moved or went is a few
// hundred bytes and goes over anything.
//
// "Of this device's own" is doing real work in both. A file downloaded from
// the server is the server's, lent: it is never offered back, and deleting the
// download is not deleting the song. And a file the server once listed and
// stopped listing (Track.WithdrawnByOrigin) is not offered again either, or no
// removal made on one device would survive another device holding a copy.
public class LibraryMirrorService
{
    // Half the server's ceiling for one piece (LibraryIngest.MaxChunkBytes).
    // A song is two or three of these; a dropped connection costs one.
    internal const int ChunkBytes = 4 * 1024 * 1024;

    // How many songs one removal request names. The route takes any number;
    // this only keeps a body small when a whole folder has gone.
    private const int RemovalBatch = 500;

    // One second past the server's own window, the way PeerSyncCoordinator
    // waits out a refused sync.
    private static readonly TimeSpan ThrottleWait = TimeSpan.FromSeconds(61);

    // A piece can be megabytes over a phone's uplink; the admin client's usual
    // minute is not enough for one.
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromMinutes(10);

    private readonly Library _library;
    private readonly AppSettings _appSettings;
    private readonly LibrarySyncService _librarySync;
    private readonly IPeerCredentials _credentials;
    private readonly ILogger _logger;

    // Files the server said no to for a reason that is about the file - a name
    // that is taken, a type it does not import. Asking again changes nothing,
    // so they are left alone until the app next starts.
    private readonly ConcurrentDictionary<Guid, byte> _refused = new();

    private int _uploading;

    // Test seam: how long to wait when the server says 429.
    internal Func<TimeSpan, CancellationToken, Task> Wait { get; set; } = Task.Delay;

    public LibraryMirrorService(
        Library library, AppSettings appSettings, LibrarySyncService librarySync, IPeerCredentials credentials,
        ILogger<LibraryMirrorService> logger)
    {
        _library = library;
        _appSettings = appSettings;
        _librarySync = librarySync;
        _credentials = credentials;
        _logger = logger;
    }

    // Raised as a round of uploads moves on: how many have gone, of how many.
    // (0, 0) when the round is over.
    public event Action<int, int>? UploadProgress;

    private bool MayMirrorTo(DiscoveredDevice device) =>
        device.WeAreAdmin && SyncRolePolicy.MayRequestFrom(_appSettings.PairedServerFingerprint, device.Fingerprint);

    private static bool IsOnServer(Track track, DiscoveredDevice device) =>
        track.OriginTrackId is { Length: > 0 } && track.OriginDeviceFingerprint == device.Fingerprint;

    // Reached on the server's own network - found on this link, or at a
    // private address - as against over a tailnet or a public one. The same
    // ranking the rest of the app decides routes by.
    internal static bool IsOnLocalNetwork(DiscoveredDevice device) => NetworkDiscoveryService.ReachRank(device) <= 1;

    // What a file looks like from the outside. Enough to notice it has been
    // written to; the server's own checksum settles whether it actually
    // changed (LibraryIngest answers "already have exactly this" for free).
    internal static string? StampOf(string path)
    {
        try
        {
            var info = new FileInfo(path);
            return info.Exists ? $"{info.Length}:{info.LastWriteTimeUtc.Ticks}" : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    // This device's own files that the server holds a copy of.
    private List<Track> OwnFilesOnServer(DiscoveredDevice device) =>
        _library.Tracks
            .Where(t => t.Path != null && !t.IsLocallyDownloaded && IsOnServer(t, device))
            .ToList();

    // What this device has that the server should: a file that is here, that a
    // scan of this device's own folders found, and that the server neither
    // has nor has had.
    internal List<Track> PendingUploads(DiscoveredDevice device) =>
        _library.Tracks
            .Where(t => t.Path != null
                        && !t.IsLocallyDownloaded
                        && !t.WithdrawnByOrigin
                        && !IsOnServer(t, device)
                        && !_refused.ContainsKey(t.Id))
            .ToList();

    // ── Files that have gone ──────────────────────────────────────────────

    // Tells the server about songs whose files have left this device's disk -
    // the records Library.UpdateTracks leaves when a scan stops finding a file
    // the server knows (RemovedTrack.OwedToOrigin). Returns how many songs the
    // server was asked to remove.
    //
    // Removed from its library, and nothing more. The server's file stays on
    // its disk, set aside from its scans, until the owner deletes it from
    // Removed Songs or puts the song back - so a file deleted here by mistake
    // costs nothing there but a click on Restore.
    //
    // Run before a catalog pull, not after: told first, the server's answer no
    // longer lists the song, and it never flickers back into this library as
    // a placeholder on its way out.
    //
    // A scan not finding a file is weak evidence, and what it is weak evidence
    // *of* is usually not a deletion - a drive that is not plugged in, a share
    // that did not mount, a folder taken out of the settings. Acting on any of
    // those would empty the server's library, and every other device's with
    // it, because a cable came loose. So a missing file is reported only when
    // the folder it lived
    // in is still there and still has something in it, which is what a
    // deletion looks like and an unmounted volume does not. Anything short of
    // that stays owed, and is asked about again next time.
    public virtual async Task<int> ReportVanishedAsync(DiscoveredDevice device, CancellationToken ct = default)
    {
        var owed = _library.RemovedTracks
            .Where(r => r.OwedToOrigin && r.Track.OriginDeviceFingerprint == device.Fingerprint)
            .ToList();
        if (owed.Count == 0)
            return 0;

        // Not this device's to say. Settled rather than left owing, so a phone
        // made an admin next year does not open by deleting whatever it lost
        // track of while it was a listener.
        if (!MayMirrorTo(device))
        {
            if (!device.WeAreAdmin)
                _library.SettleRemovedTracks(owed.Select(r => r.Track.Id).ToList());
            return 0;
        }

        var roots = Importer.Importer.ScanRoots(_appSettings.LibraryPaths);
        var rootIsThere = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
        var stillHere = _library.Tracks
            .Where(t => t.Path != null && IsOnServer(t, device))
            .Select(t => t.OriginTrackId!)
            .ToHashSet();

        var gone = new List<RemovedTrack>();
        var notNews = new List<Guid>();
        foreach (var removed in owed)
        {
            var path = removed.Track.Path;

            // The song is still here as a file - found again somewhere else,
            // or back where it was - or was never under a folder this device
            // scans now. Either way there is no deletion to report.
            if (path == null || File.Exists(path) || stillHere.Contains(removed.Track.OriginTrackId!)
                || LibraryFolders.RootOf(path, roots) is not { } root)
            {
                notNews.Add(removed.Track.Id);
                continue;
            }

            if (!rootIsThere.TryGetValue(root, out var there))
                rootIsThere[root] = there = IsPresent(root);

            if (there)
                gone.Add(removed);
        }

        _library.SettleRemovedTracks(notNews);
        if (gone.Count == 0)
            return 0;

        var reported = 0;
        try
        {
            using var http = PeerHttpClient.Create(TimeSpan.FromMinutes(1));
            var client = new ServerAdminClient(http, device.BaseUri, ServerAdminClient.SignWith(_credentials), logger: _logger);
            foreach (var batch in gone.Chunk(RemovalBatch))
            {
                // Without the files: out of the server's library, and so out
                // of every device's, with the server's copy left on its disk
                // for its owner to delete or restore.
                var response = await client.RemoveFromLibraryAsync(
                    new LibraryRemovalRequestDto(batch.Select(r => r.Track.OriginTrackId!).Distinct().ToList(), DeleteFiles: false), ct);
                _library.SettleRemovedTracks(batch.Select(r => r.Track.Id).ToList());
                reported += batch.Length;

                _logger.LogInformation(
                    "{Count} song(s) deleted from this device were removed from {Server}'s library too ({Removed} were still in it); its files are kept until they are cleaned up there",
                    batch.Length, device.Alias, response.Removed);
            }
        }
        catch (Exception ex) when (ex is ServerAdminException or HttpRequestException or TaskCanceledException)
        {
            // Still owed, and said again at the next sync.
            _logger.LogWarning(ex, "Could not tell {Server} about {Count} song(s) deleted from this device; will try again",
                device.Alias, gone.Count - reported);
        }

        return reported;
    }

    // There, and not an empty mount point - which is what a share that failed
    // to mount leaves behind on Linux, and reads as "every file deleted".
    private static bool IsPresent(string root)
    {
        try
        {
            return Directory.Exists(root) && Directory.EnumerateFileSystemEntries(root).Any();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    // ── Tags edited here ──────────────────────────────────────────────────

    // How many songs' tags one request carries. Editing an album is a dozen;
    // this only bounds a body when a whole library was retagged offline.
    private const int TagEditBatch = 100;

    // Sends the server the tags of every song edited on this device since the
    // server last had them (Track.TagsEditedAt) - a song this device has a file
    // of, one it downloaded, or one it only streams; an edit made in Track Info
    // is an edit whichever it was. The server writes them into its own file,
    // and its catalog then carries them to every other device. Returns how
    // many edits the server took.
    //
    // The values, not the file, which is what makes this work from a phone on
    // a train: a few hundred bytes a song, over any connection, where sending
    // the file again would wait for home. A device that does have the file
    // sends that too, in its own time (UploadPendingAsync) - a tag edit is
    // also a changed file - and the server finds it already says the same.
    public virtual async Task<int> PushTagEditsAsync(DiscoveredDevice device, CancellationToken ct = default)
    {
        if (!MayMirrorTo(device) || !_librarySync.HasCurrentCatalogFrom(device.Fingerprint))
            return 0;

        var owed = _library.Tracks
            .Where(t => IsOnServer(t, device)
                        && t.TagsEditedAt is { } mine
                        && !(_librarySync.ServerTagsEditedAt(device.Fingerprint, t.OriginTrackId!) is { } theirs && theirs >= mine))
            .ToList();
        if (owed.Count == 0)
            return 0;

        var taken = 0;
        try
        {
            using var http = PeerHttpClient.Create(TimeSpan.FromMinutes(2));
            var client = new ServerAdminClient(http, device.BaseUri, ServerAdminClient.SignWith(_credentials), logger: _logger);
            foreach (var batch in owed.Chunk(TagEditBatch))
            {
                var edits = batch
                    .Select(t => new TrackTagEditDto(t.OriginTrackId!, t.TagsEditedAt!.Value, TrackTags.Of(t)))
                    .ToList();
                var response = await Throttled(() => client.EditTagsAsync(new LibraryTagEditsRequestDto(edits), ct), ct);
                _librarySync.NoteLibraryTokenCausedHere(device.Fingerprint, response.LibraryToken);

                // Settled, whether the server applied it or already had
                // something as new - except where it could not write its file,
                // which is an edit that did not happen there and is said again.
                var notWritten = response.NotWritten.ToHashSet();
                foreach (var edit in edits)
                {
                    if (!notWritten.Contains(edit.TrackId))
                        _librarySync.NoteServerTagsEditedAt(device.Fingerprint, edit.TrackId, edit.EditedAt);
                }

                AcceptAsSent(batch.Where(t => !notWritten.Contains(t.OriginTrackId!)));

                taken += response.Applied;
                if (notWritten.Count > 0)
                {
                    _logger.LogWarning("{Server} could not write the edited tags of {Count} song(s) into its files; will send them again",
                        device.Alias, notWritten.Count);
                }
            }

            _logger.LogInformation("Sent the edited tags of {Count} song(s) to {Server}; it applied {Taken}", owed.Count, device.Alias, taken);
        }
        catch (Exception ex) when (ex is ServerAdminException or HttpRequestException or TaskCanceledException && !ct.IsCancellationRequested)
        {
            // Still owed: the edit is on the track, and is compared again next time.
            _logger.LogWarning(ex, "Could not send edited tags to {Server}; will try again", device.Alias);
        }

        return taken;
    }

    // The edit made to these files has reached the server as what it was - new
    // tags, a new picture - so the files are no longer "changed since the
    // server had them" (Track.OriginFileStamp), though editing them changed
    // them. Without this, every retitle would be followed by the whole file
    // going up as a new version, and a new version is something every other
    // device fetches in full (Track.FileReplacedAt): an album's worth of
    // downloads, per device, for one corrected title.
    //
    // Only for files this device uploads changes to - its own, stamped ones.
    private void AcceptAsSent(IEnumerable<Track> tracks)
    {
        var restamped = new List<(Track Track, string? FileStamp, bool ClearMove)>();
        foreach (var track in tracks)
        {
            if (track is { Path: { } path, IsLocallyDownloaded: false, OriginFileStamp: not null } && StampOf(path) is { } stamp)
                restamped.Add((track, stamp, false));
        }

        _library.RecordOriginBookkeeping(restamped);
    }

    // How many songs one artwork request names. Their ids ride in the query,
    // which has a length; an album is well inside this.
    private const int ArtEditBatch = 40;

    // The artwork twin of PushTagEditsAsync: sends the server the picture of
    // every song whose artwork was changed on this device since the server
    // last had it (Track.ArtEditedAt), read out of this device's own file. One
    // request per distinct picture, naming every song that now carries it -
    // an album's cover is one image and a dozen files, and is sent once.
    //
    // Only for songs this device has a file of. Changing the cover of a song
    // it merely streams is done on the server directly (AlbumArtEditor), so
    // there is nothing left over to send.
    public virtual async Task<int> PushArtEditsAsync(DiscoveredDevice device, CancellationToken ct = default)
    {
        if (!MayMirrorTo(device) || !_librarySync.HasCurrentCatalogFrom(device.Fingerprint))
            return 0;

        var owed = _library.Tracks
            .Where(t => t.Path != null
                        && IsOnServer(t, device)
                        && t.ArtEditedAt is { } mine
                        && !(_librarySync.ServerArtEditedAt(device.Fingerprint, t.OriginTrackId!) is { } theirs && theirs >= mine))
            .ToList();
        if (owed.Count == 0)
            return 0;

        var taken = 0;
        try
        {
            using var http = PeerHttpClient.Create(TimeSpan.FromMinutes(2));
            var client = new ServerAdminClient(http, device.BaseUri, ServerAdminClient.SignWith(_credentials), logger: _logger);

            // By picture and by date: the songs of one edit share both.
            var groups = owed
                .Select(t => (Track: t, Art: LocalAlbumArtReader.EmbeddedIn(t.Path!)))
                .GroupBy(x => (
                    Picture: x.Art == null ? "" : Convert.ToHexStringLower(SHA256.HashData(x.Art.Bytes)),
                    At: x.Track.ArtEditedAt!.Value));

            foreach (var group in groups)
            {
                var art = group.First().Art;
                foreach (var batch in group.Select(x => x.Track).Chunk(ArtEditBatch))
                {
                    var ids = batch.Select(t => t.OriginTrackId!).ToList();
                    var response = await Throttled(() => art != null
                        ? client.SetArtworkAsync(ids, group.Key.At, art.Bytes, art.MimeType, ct)
                        : client.RemoveArtworkAsync(ids, group.Key.At, ct), ct);
                    _librarySync.NoteLibraryTokenCausedHere(device.Fingerprint, response.LibraryToken);

                    var notWritten = response.NotWritten.ToHashSet();
                    foreach (var id in ids)
                    {
                        if (!notWritten.Contains(id))
                            _librarySync.NoteServerArtEditedAt(device.Fingerprint, id, group.Key.At);
                    }

                    AcceptAsSent(batch.Where(t => !notWritten.Contains(t.OriginTrackId!)));

                    taken += response.Applied;
                }
            }

            _logger.LogInformation("Sent the changed artwork of {Count} song(s) to {Server}; it applied {Taken}", owed.Count, device.Alias, taken);
        }
        catch (Exception ex) when (ex is ServerAdminException or HttpRequestException or TaskCanceledException && !ct.IsCancellationRequested)
        {
            _logger.LogWarning(ex, "Could not send changed artwork to {Server}; will try again", device.Alias);
        }

        return taken;
    }

    // ── Files the server does not have ────────────────────────────────────

    // One round of bringing the server's files into step with this device's:
    // moves first, then every file the server lacks, then every file that has
    // changed here since the server got it. Only one round runs at a time; a
    // second caller gets "nothing" straight back rather than a second set of
    // the same requests.
    //
    // The list of new files is re-read before every upload rather than taken
    // once, for two reasons a long round makes real. A rescan can land in the
    // middle of one and its new files belong in it. And the same rescan means
    // the matching this list rests on is stale
    // (LibrarySyncService.HasCurrentCatalogFrom): a new file may well be a
    // song the server already has, and until a pull has compared them,
    // uploading it would be guessing. The round stops there, and the sync that
    // rescan schedules starts the next one.
    public virtual async Task<LibraryUploadSummary> UploadPendingAsync(DiscoveredDevice device, CancellationToken ct = default)
    {
        if (!MayMirrorTo(device) || Interlocked.CompareExchange(ref _uploading, 1, 0) != 0)
            return LibraryUploadSummary.Nothing;

        var uploaded = 0;
        var refused = 0;
        var moved = 0;
        string? stopped = null;
        try
        {
            if (!_librarySync.HasCurrentCatalogFrom(device.Fingerprint))
                return LibraryUploadSummary.Nothing;

            var roots = Importer.Importer.ScanRoots(_appSettings.LibraryPaths);
            using var http = PeerHttpClient.Create(RequestTimeout);
            var client = new ServerAdminClient(http, device.BaseUri, ServerAdminClient.SignWith(_credentials), logger: _logger);

            moved = await TellMovesAsync(client, device, roots, ct);
            var changed = ChangedSinceSent(device);

            var waiting = PendingUploads(device).Count + changed.Count;
            if (waiting == 0)
                return new LibraryUploadSummary(0, 0, 0, Moved: moved);

            // Files travel on the server's own network unless the user has
            // said otherwise. Said once, not on every sync - the songs are
            // still waiting the next time, and so is the reason.
            if (!IsOnLocalNetwork(device) && !_appSettings.UploadWhenAwayFromHome)
            {
                if (!_saidWaitingForHome)
                {
                    _saidWaitingForHome = true;
                    _logger.LogInformation(
                        "{Count} song(s) are waiting to be sent to {Server}; this device is not on its network, and uploading away from home is off",
                        waiting, device.Alias);
                }

                return new LibraryUploadSummary(0, 0, waiting, "not on the server's network", moved);
            }

            _saidWaitingForHome = false;
            _logger.LogInformation("{Count} song(s) on this device are new or changed since {Server} last had them; uploading",
                waiting, device.Alias);

            var attempted = new HashSet<Guid>();
            while (stopped == null)
            {
                ct.ThrowIfCancellationRequested();

                if (!MayMirrorTo(device))
                {
                    stopped = "this device is no longer paired with that server as an admin";
                    break;
                }
                if (!_librarySync.HasCurrentCatalogFrom(device.Fingerprint))
                {
                    stopped = "the library changed; the next sync carries on";
                    break;
                }

                // New songs first: a song the server does not have at all is
                // worth more than a tag correction to one it does.
                var pending = PendingUploads(device);
                var track = pending.FirstOrDefault(t => !attempted.Contains(t.Id));
                var replaces = false;
                if (track == null)
                {
                    track = changed.FirstOrDefault(t => !attempted.Contains(t.Id));
                    replaces = true;
                }
                if (track == null)
                    break;

                attempted.Add(track.Id);
                var left = pending.Count + changed.Count(t => !attempted.Contains(t.Id)) + (replaces ? 1 : 0);
                UploadProgress?.Invoke(uploaded + refused, uploaded + refused + left);

                switch (await UploadOneAsync(client, device, track, roots, replaces, ct))
                {
                    case UploadResult.Uploaded:
                        uploaded++;
                        break;
                    case UploadResult.Refused:
                        refused++;
                        _refused[track.Id] = 0;
                        break;
                    case UploadResult.Skipped:
                        break;
                    case UploadResult.ServerCannotTakeFiles:
                        stopped = "the server cannot take files right now";
                        break;
                    case UploadResult.Unreachable:
                        stopped = "the server stopped answering";
                        break;
                }
            }

            var remaining = PendingUploads(device).Count;
            _logger.LogInformation(
                "Upload to {Server} finished: {Uploaded} uploaded, {Refused} refused, {Remaining} still waiting{Stopped}",
                device.Alias, uploaded, refused, remaining, stopped == null ? "" : $" ({stopped})");
            return new LibraryUploadSummary(uploaded, refused, remaining, stopped, moved);
        }
        finally
        {
            UploadProgress?.Invoke(0, 0);
            Interlocked.Exchange(ref _uploading, 0);
        }
    }

    private bool _saidWaitingForHome;

    // Which of this device's own files have been written to since the server
    // got them - and, in the same pass, the first look at any file the two
    // already had in common, which is taken as "the same" and stamped. That
    // second half is the baseline everything later is measured against: a song
    // both ends held before they ever met is not re-sent on the strength of a
    // modification time from years ago.
    private List<Track> ChangedSinceSent(DiscoveredDevice device)
    {
        var changed = new List<Track>();
        var firstSeen = new List<(Track Track, string? FileStamp, bool ClearMove)>();
        foreach (var track in OwnFilesOnServer(device))
        {
            if (StampOf(track.Path!) is not { } stamp)
                continue;

            if (track.OriginFileStamp == null)
                firstSeen.Add((track, stamp, false));
            else if (track.OriginFileStamp != stamp && !_refused.ContainsKey(track.Id))
                changed.Add(track);
        }

        _library.RecordOriginBookkeeping(firstSeen);
        return changed;
    }

    // Songs whose file a scan found somewhere new (Track.MovedFromPath): the
    // server is asked to move its copy to the same place. One small request a
    // song, sent over any connection. Returns how many the server moved.
    private async Task<int> TellMovesAsync(
        ServerAdminClient client, DiscoveredDevice device, IReadOnlyList<string> roots, CancellationToken ct)
    {
        var moved = 0;
        var notMoves = new List<(Track Track, string? FileStamp, bool ClearMove)>();
        var comparison = OperatingSystem.IsLinux() ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;
        foreach (var track in OwnFilesOnServer(device).Where(t => t.MovedFromPath != null))
        {
            if (LibraryDtoMapper.RelativePathOf(track, roots) is not { } relativePath)
                continue;

            // Where the server already has it, or the same place below a
            // library folder that has itself moved - a renamed music folder,
            // or iOS handing the app a new container. The file did not move
            // within the library, which is the only kind of move there is to
            // tell anyone about.
            var from = track.MovedFromPath!.Replace('\\', '/');
            if (relativePath == track.OriginRelativePath || from.EndsWith("/" + relativePath, comparison))
            {
                notMoves.Add((track, null, true));
                continue;
            }

            try
            {
                var response = await Throttled(() => client.MoveInLibraryAsync(
                    new LibraryMoveRequestDto(track.OriginTrackId!, relativePath), ct), ct);
                _library.RecordOrigin(track, device.Fingerprint, track.OriginTrackId!, response.RelativePath, null);
                _librarySync.NoteLibraryTokenCausedHere(device.Fingerprint, response.LibraryToken);
                moved++;
                _logger.LogDebug("{Server} moved its copy of {Path} to {RelativePath}", device.Alias, LogPath.Short(track.Path!), relativePath);
            }
            catch (ServerAdminException ex) when (ex.Status is HttpStatusCode.Conflict or HttpStatusCode.BadRequest or HttpStatusCode.NotFound)
            {
                // About this file, and final: the name is taken there, or the
                // server will not write it. Its copy stays where it was, and
                // this is not asked again.
                _logger.LogWarning("{Server} would not move its copy of {Path}: {Reason}", device.Alias, LogPath.Short(track.Path!), ex.Message);
                notMoves.Add((track, null, true));
            }
            catch (Exception ex) when (ex is ServerAdminException or HttpRequestException or TaskCanceledException && !ct.IsCancellationRequested)
            {
                // About the server. The rest wait, still marked, for next time.
                _logger.LogWarning(ex, "Could not tell {Server} about moved files; will try again", device.Alias);
                break;
            }
        }

        _library.RecordOriginBookkeeping(notMoves);
        if (moved > 0)
            _logger.LogInformation("{Count} song(s) moved on this device were moved on {Server} too", moved, device.Alias);
        return moved;
    }

    private enum UploadResult
    {
        Uploaded,

        // The server will not have this file, whatever is tried.
        Refused,

        // Not uploaded and not refused: unreadable here, or failed in a way
        // worth another go next round.
        Skipped,

        ServerCannotTakeFiles,
        Unreachable,
    }

    // replaces is true for a new version of a song the server already has:
    // the same exchange, naming the song it replaces, after which the song is
    // the same song with these bytes.
    private async Task<UploadResult> UploadOneAsync(
        ServerAdminClient client, DiscoveredDevice device, Track track, IReadOnlyList<string> roots, bool replaces,
        CancellationToken ct)
    {
        var path = track.Path!;
        var relativePath = LibraryDtoMapper.RelativePathOf(track, roots);
        if (relativePath == null)
            return UploadResult.Skipped;

        // Twice at most. The second go is for the two refusals that are about
        // the attempt rather than the file: a server that restarted and forgot
        // the upload (404), and bytes that did not arrive intact (422).
        for (var attempt = 1; attempt <= 2; attempt++)
        {
            try
            {
                // Taken before the file is read, so a write that lands while
                // it is being sent leaves it looking changed, and it goes again.
                var stamp = StampOf(path);
                long length;
                string sha256;
                await using (var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16, useAsync: true))
                {
                    length = file.Length;
                    sha256 = Convert.ToHexStringLower(await SHA256.HashDataAsync(file, ct));
                }

                var status = await Throttled(() => client.BeginUploadAsync(
                    new LibraryUploadRequestDto(relativePath, length, sha256, track.DateAdded,
                        replaces ? track.OriginTrackId : null), ct), ct);

                await using (var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16, useAsync: true))
                {
                    // Every answer says where the server has got to, and the
                    // next piece starts there - so a server that already holds
                    // half the file from an earlier attempt is sent the other
                    // half. An answer that does not move is a server and a
                    // device disagreeing about the file, and is not argued with.
                    var stalls = 0;
                    while (status.UploadId is { } uploadId)
                    {
                        var offset = status.Offset;
                        if (offset < 0 || offset >= length)
                            return UploadResult.Skipped;

                        var chunk = new byte[(int)Math.Min(ChunkBytes, length - offset)];
                        file.Position = offset;
                        await file.ReadExactlyAsync(chunk, ct);

                        status = await Throttled(() => client.UploadChunkAsync(uploadId, offset, chunk, ct), ct);
                        if (status.UploadId != null && status.Offset <= offset && ++stalls >= 3)
                            return UploadResult.Skipped;
                    }
                }

                if (status.TrackId is not { Length: > 0 } trackId)
                    return UploadResult.Skipped;

                // A replacement leaves the song where the server keeps it; only
                // a new song is known to be at this device's own path.
                _library.RecordOrigin(
                    track, device.Fingerprint, trackId, replaces ? track.OriginRelativePath : relativePath,
                    status.DateAdded, stamp, status.TagsEditedAt, status.ArtEditedAt, status.FileReplacedAt);
                if (status.TagsEditedAt is { } editedAt)
                    _librarySync.NoteServerTagsEditedAt(device.Fingerprint, trackId, editedAt);
                if (status.ArtEditedAt is { } repaintedAt)
                    _librarySync.NoteServerArtEditedAt(device.Fingerprint, trackId, repaintedAt);
                _librarySync.NoteLibraryTokenCausedHere(device.Fingerprint, status.LibraryToken);
                _logger.LogDebug("Uploaded {Path} to {Server} as {TrackId}", LogPath.Short(path), device.Alias, trackId);
                return UploadResult.Uploaded;
            }
            catch (ServerAdminException ex) when (ex.Status is HttpStatusCode.NotFound or HttpStatusCode.UnprocessableEntity)
            {
                if (attempt == 2)
                {
                    _logger.LogWarning("Uploading {Path} to {Server} failed twice: {Reason}", LogPath.Short(path), device.Alias, ex.Message);
                    return UploadResult.Skipped;
                }
            }
            catch (ServerAdminException ex) when (ex.Status is HttpStatusCode.Conflict or HttpStatusCode.BadRequest)
            {
                _logger.LogWarning("{Server} would not take {Path}: {Reason}", device.Alias, LogPath.Short(path), ex.Message);
                return UploadResult.Refused;
            }
            catch (ServerAdminException ex)
            {
                // 503 (nowhere to write), 401/403 (no longer an admin there),
                // or a 429 that outlasted its wait: none of them is about this
                // file, and the next one would be told the same.
                _logger.LogWarning("{Server} is not taking uploads: {Reason}", device.Alias, ex.Message);
                return UploadResult.ServerCannotTakeFiles;
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException && !ct.IsCancellationRequested)
            {
                _logger.LogWarning(ex, "Lost {Server} while uploading {Path}; the rest waits for the next sync", device.Alias, LogPath.Short(path));
                return UploadResult.Unreachable;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // The file went, or cannot be read, between being listed and
                // being opened. The next scan says which.
                _logger.LogDebug(ex, "Could not read {Path} to upload it", LogPath.Short(path));
                return UploadResult.Skipped;
            }
        }

        return UploadResult.Skipped;
    }

    // A 429 is the server asking for a pause, and the answer is to pause - once
    // or twice. A third is handed to the caller, which stops the round.
    private async Task<T> Throttled<T>(Func<Task<T>> send, CancellationToken ct)
    {
        for (var waits = 0; ; waits++)
        {
            try
            {
                return await send();
            }
            catch (ServerAdminException ex) when (ex.Status == HttpStatusCode.TooManyRequests && waits < 2)
            {
                _logger.LogDebug("The server asked for fewer upload requests; waiting {Wait}", ThrottleWait);
                await Wait(ThrottleWait, ct);
            }
        }
    }
}
