using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;

using Microsoft.Extensions.Logging;

using Flower.Logging;
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
            var destination = ResolveDestination(track, folder);
            Directory.CreateDirectory(Path.GetDirectoryName(destination) ?? folder);

            await client.DownloadTrackAsync(originTrackId, destination);

            // One upsert of the one row whose Path changed. This used to
            // rewrite all 16k rows to push a single field.
            track.Path = destination;
            // Set together with Path, because this - not the presence of an
            // origin fingerprint - is what tells the next rescan that no folder
            // scan is responsible for this file, wherever it ended up. See
            // Track.IsLocallyDownloaded and Library.UpdateTracks.
            track.IsLocallyDownloaded = true;
            ReadTechnicalProperties(track);
            _library.NotifyTrackChanged(track);

            _logger.LogInformation("Downloaded {Title} ({OriginTrackId}) from {Alias} to {Destination}",
                track.Title, originTrackId, peer.Alias, destination);

            return TrackDownloadResult.Downloaded;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Download failed for {Title} ({OriginTrackId}) from {Alias} ({Fingerprint}) at {EndPoint}",
                track.Title, originTrackId, peer.Alias, track.OriginDeviceFingerprint, peer.BaseUri);
            return TrackDownloadResult.Failed;
        }
    }

    // Where the downloaded file goes, and what it is called.
    //
    // Mirrors the origin's own tree under the download folder - "Angine de
    // Poitrine/Vol.II/01 Fabienk.mp3" - so a downloaded library is browsable,
    // greppable and movable by hand like any other music folder. Before this
    // every download was named after this device's Track.Id and dropped flat
    // into one directory: "904740018a1d4b22bdb45d9a9b84c7fb.mp3", meaningless
    // to anything that is not Flower reading this device's database.
    //
    // Deterministic per track, which the id-based name got right and this has
    // to keep: an interrupted download leaves a "<destination>.part" beside it,
    // and OpenSubsonicClient.DownloadTrackAsync can only resume from that if
    // the next attempt picks the same name.
    //
    // Falls back to the old id-based name whenever the origin sent no relative
    // path (a third-party server - see Track.OriginRelativePath) or sent one
    // that sanitizes away to nothing.
    private string ResolveDestination(Track track, string folder)
    {
        var extension = string.IsNullOrEmpty(track.OriginFileExtension) ? "mp3" : track.OriginFileExtension;
        var fallback = Path.Combine(folder, $"{track.Id:N}.{extension}");

        if (SafeRelativePath(track.OriginRelativePath) is not { } relative)
            return fallback;

        var destination = Path.Combine(folder, relative);

        // Two tracks whose relative paths collide cannot both live here. On the
        // origin they were distinct files, so this only happens across two
        // origins or after a sanitize - rare, but silently overwriting one
        // track's audio with another's is not an acceptable way to find out.
        // The id goes back in for the loser, which is unique by construction.
        if (_library.Tracks.Any(t => !ReferenceEquals(t, track)
                && t.Path != null
                && string.Equals(t.Path, destination, StringComparison.OrdinalIgnoreCase)))
        {
            return fallback;
        }

        return destination;
    }

    // The relative path arrives from a peer, so it is untrusted input being
    // turned into a filesystem path - the classic traversal shape. Rebuilt
    // segment by segment rather than filtered: anything that is not a plain
    // name ("..", ".", an absolute root, a drive letter, a character the
    // platform forbids in a file name) cannot survive reconstruction, so there
    // is no escaping trick to miss. Returns null when nothing usable is left.
    private static string? SafeRelativePath(string? relative)
    {
        if (string.IsNullOrWhiteSpace(relative))
            return null;

        var invalid = Path.GetInvalidFileNameChars();
        var segments = new List<string>();
        foreach (var raw in relative.Split(['/', '\\'], StringSplitOptions.RemoveEmptyEntries))
        {
            var segment = new string(raw.Where(c => !invalid.Contains(c)).ToArray()).Trim();
            if (segment.Length == 0 || segment == "." || segment == "..")
                continue;

            // Comfortably under every platform's per-component limit (255 on
            // APFS/ext4/NTFS), and long enough that no real track name reaches
            // it.
            segments.Add(segment.Length > 200 ? segment[..200] : segment);
        }

        return segments.Count == 0 ? null : Path.Combine([.. segments]);
    }

    // The file is local now, so read what it actually is off the bytes rather
    // than keeping what the origin said about its own copy. Nothing else will:
    // a downloaded file deliberately lands outside every configured library
    // folder (see ResolveDownloadFolder), which is the whole reason
    // Library.UpdateTracks carries it forward instead of rediscovering it - so
    // no folder scan is ever going to visit this path and fill these in.
    //
    // Before this, a downloaded track kept whatever the sync manifest carried,
    // and until Child grew the technical fields that was nothing at all: an
    // all-"-" Technical tab on a track whose file was sitting right there.
    // Duration is deliberately not re-read - it is part of Track.SyncKey, and a
    // second reading that rounded differently would fragment the track in every
    // subsequent merge.
    private void ReadTechnicalProperties(Track track)
    {
        if (track.Path == null)
            return;

        try
        {
            Importer.AudioTechnicalProperties.Read(track.Path).ApplyTo(track);
        }
        catch (Exception ex)
        {
            // Not a failed download - the audio is on disk and playable. The
            // Technical tab keeps whatever the origin sent, which is what it
            // showed before this read existed.
            _logger.LogDebug(ex, "Could not read audio properties of downloaded file {Path}", LogPath.Short(track.Path));
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
            _logger.LogWarning(ex, "Failed to delete downloaded file for {Title} ({Path})", track.Title, LogPath.Short(track.Path));
        }

        track.Path = null;
        track.IsLocallyDownloaded = false;
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
