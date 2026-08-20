using System;
using System.IO;
using System.Threading.Tasks;

using Microsoft.Extensions.Logging;

using Flower.Models;
using Flower.Persistence;

namespace Flower.Services;

public enum TrackDownloadResult { AlreadyDownloaded, PeerUnavailable, Downloaded, Failed }

// Downloads one placeholder track's real audio from whichever peer currently
// holds it (Track.OriginDeviceFingerprint) - see SYNC-PLAN.md Phase 3's "mobile
// download button". Resolving *which* peer, and whether it's currently reachable
// at all, is the caller's job (MainViewModel, which already tracks currently
// discovered devices via the Devices sidebar) - this service only does the I/O
// once it's handed a specific peer to talk to.
public class LibraryDownloadService
{
    private readonly Library _library;
    private readonly DeviceIdentity _deviceIdentity;
    private readonly DeviceSigningKey _signingKey;
    private readonly AppSettings _appSettings;
    private readonly ILogger<LibraryDownloadService> _logger;

    public LibraryDownloadService(Library library, DeviceIdentity deviceIdentity, DeviceSigningKey signingKey, AppSettings appSettings, ILogger<LibraryDownloadService> logger)
    {
        _library = library;
        _deviceIdentity = deviceIdentity;
        _signingKey = signingKey;
        _appSettings = appSettings;
        _logger = logger;
    }

    public async Task<TrackDownloadResult> DownloadAsync(Track track, DiscoveredDevice? peer)
    {
        if (track.Path != null)
            return TrackDownloadResult.AlreadyDownloaded;
        if (peer == null)
            return TrackDownloadResult.PeerUnavailable;
        // See Track.OriginTrackId - the peer's own id for this track, which is
        // the only thing /rest/download will match. A placeholder always has
        // one (LibrarySyncMapper.ToPlaceholderTrack); a track that never came
        // from a peer isn't downloadable by definition.
        if (track.OriginTrackId is not { } originTrackId)
        {
            _logger.LogWarning("Cannot download {Title}: it carries no origin track id", track.Title);
            return TrackDownloadResult.Failed;
        }

        try
        {
            var client = PeerOpenSubsonicClientFactory.Create(peer, _deviceIdentity, _appSettings, _signingKey);

            var folder = ResolveDownloadFolder();
            Directory.CreateDirectory(folder);
            var extension = string.IsNullOrEmpty(track.OriginFileExtension) ? "mp3" : track.OriginFileExtension;
            // Deterministic per track, not a fresh Guid per attempt: an
            // interrupted download leaves a "<destination>.part" beside it,
            // and OpenSubsonicClient.DownloadTrackAsync can only resume from
            // that if the next attempt picks the same name. A random name per
            // attempt orphaned every partial instead, in a folder nothing
            // ever cleans up.
            var destination = Path.Combine(folder, $"{track.Id:N}.{extension}");

            await client.DownloadTrackAsync(originTrackId, destination);

            // One upsert of the one row whose Path changed. This used to
            // rewrite all 16k rows to push a single field.
            track.Path = destination;
            _library.NotifyTrackChanged(track);

            _logger.LogInformation("Downloaded {Title} ({OriginTrackId}) from {Alias} to {Destination}",
                track.Title, originTrackId, peer.Alias, destination);

            return TrackDownloadResult.Downloaded;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Download failed for {Title} ({OriginTrackId}) from {Alias} ({Fingerprint}) at {EndPoint}",
                track.Title, originTrackId, peer.Alias, track.OriginDeviceFingerprint, peer.EndPoint);
            return TrackDownloadResult.Failed;
        }
    }

    // Reverts a track back to a placeholder (Path == null) and deletes the
    // local file - the counterpart to DownloadAsync above, freeing the
    // storage it used without forgetting the track. OriginDeviceFingerprint
    // is left untouched either way, so if it's set to a peer that still has
    // this exact track, the (now-placeholder) track can be re-downloaded or
    // streamed on demand from there afterward, exactly like any other not-
    // yet-downloaded synced track - if it's null (a purely local import with
    // no known peer copy), it just becomes a permanently-undownloadable
    // placeholder instead, which is why the mobile UI warns first for that
    // case (see MobileMainViewModel.IsRecoverableDownload) rather than
    // gating this outright - deleting a file that won't come back is still a
    // choice the user should be able to make, just not by accident.
    public async Task DeleteDownloadedFileAsync(Track track)
    {
        if (track.Path == null)
            return;

        try
        {
            File.Delete(track.Path);
        }
        catch (Exception ex)
        {
            // Still proceed to revert to a placeholder below even if the file
            // is already gone/inaccessible - a failed delete of a file that
            // doesn't exist anymore isn't a reason to leave Path pointing at it.
            _logger.LogWarning(ex, "Failed to delete downloaded file for {Title} ({Path})", track.Title, track.Path);
        }

        track.Path = null;
        _library.NotifyTrackChanged(track);
    }

    // Test-only override, checked first below. Unlike AppDataDirectory (see
    // PlatformDataDirectory.Current), SpecialFolder.MyMusic/.Personal resolve
    // via native OS APIs that don't respect a HOME env var override on
    // macOS - without this, a test exercising DownloadAsync's success path
    // would actually write into the real developer's ~/Music. Left null
    // everywhere else.
    public static string? DownloadFolderOverride { get; set; }

    // Same folders Importer/AndroidMediaStoreImporter already treat as this
    // platform's own music location (see Importer.ResolveMusicPath) - except on
    // Android, where a downloaded file deliberately lives in app-private storage
    // rather than anywhere MediaStore would index it (MediaStore is a read-only
    // system index Flower can't easily insert into); Library.UpdateTracks' carry-
    // forward is what keeps such a file known across rescans on that platform,
    // not rediscovery. Not yet verified on a real Android device.
    private static string ResolveDownloadFolder()
    {
        if (DownloadFolderOverride != null)
            return DownloadFolderOverride;
        if (OperatingSystem.IsIOS())
            return Environment.GetFolderPath(Environment.SpecialFolder.Personal);
        if (OperatingSystem.IsAndroid() && PlatformDataDirectory.Current is { } androidRoot)
            return Path.Combine(androidRoot, "Downloads");

        return Environment.GetFolderPath(Environment.SpecialFolder.MyMusic);
    }
}
