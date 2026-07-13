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
    private readonly LibraryStore _libraryStore;
    private readonly ILogger<LibraryDownloadService> _logger;

    public LibraryDownloadService(Library library, DeviceIdentity deviceIdentity, LibraryStore libraryStore, ILogger<LibraryDownloadService> logger)
    {
        _library = library;
        _deviceIdentity = deviceIdentity;
        _libraryStore = libraryStore;
        _logger = logger;
    }

    public async Task<TrackDownloadResult> DownloadAsync(Track track, DiscoveredDevice? peer)
    {
        if (track.Path != null)
            return TrackDownloadResult.AlreadyDownloaded;
        if (peer == null)
            return TrackDownloadResult.PeerUnavailable;

        try
        {
            var client = new OpenSubsonicClient(
                $"http://{peer.EndPoint}", username: "", password: "",
                extraHeaders:
                [
                    ("X-Flower-Fingerprint", _deviceIdentity.Fingerprint),
                    ("X-Flower-Alias", _deviceIdentity.Alias),
                ]);

            var folder = ResolveDownloadFolder();
            Directory.CreateDirectory(folder);
            var extension = string.IsNullOrEmpty(track.OriginFileExtension) ? "mp3" : track.OriginFileExtension;
            var destination = Path.Combine(folder, $"{Guid.NewGuid():N}.{extension}");

            await client.DownloadTrackAsync(track.SyncKey, destination);

            track.Path = destination;
            await _libraryStore.SaveAsync(_library.Tracks);
            _library.NotifyTrackChanged();

            return TrackDownloadResult.Downloaded;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Download failed for {Title} ({SyncKey}) from {Alias} ({Fingerprint}) at {EndPoint}",
                track.Title, track.SyncKey, peer.Alias, track.OriginDeviceFingerprint, peer.EndPoint);
            return TrackDownloadResult.Failed;
        }
    }

    // Same folders Importer/AndroidMediaStoreImporter already treat as this
    // platform's own music location (see Importer.ResolveMusicPath) - except on
    // Android, where a downloaded file deliberately lives in app-private storage
    // rather than anywhere MediaStore would index it (MediaStore is a read-only
    // system index Flower can't easily insert into); Library.UpdateTracks' carry-
    // forward is what keeps such a file known across rescans on that platform,
    // not rediscovery. Not yet verified on a real Android device.
    private static string ResolveDownloadFolder()
    {
        if (OperatingSystem.IsIOS())
            return Environment.GetFolderPath(Environment.SpecialFolder.Personal);
        if (OperatingSystem.IsAndroid() && PlatformDataDirectory.Current is { } androidRoot)
            return Path.Combine(androidRoot, "Downloads");

        return Environment.GetFolderPath(Environment.SpecialFolder.MyMusic);
    }
}
