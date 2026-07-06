using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using Flower.Models;

namespace Flower.Services;

// Pulls a peer's full track catalog and merges anything this device doesn't
// already have as Path == null placeholders - see SYNC-PLAN.md Phase 3. Reuses
// OpenSubsonicClient (the same client Flower will use to talk to a real
// Navidrome/Jellyfin server) against the peer's own embedded SyncHttpServer
// host, authenticated via the trust-gate identity headers rather than real
// Subsonic credentials (see SyncHttpServer.AuthorizeAsync).
//
// Unlike PlaylistSyncService, both sides of a discovered pair run this
// independently rather than electing one initiator: there's no write-back to
// the peer here, just a local, additive merge, so there's no risk of two
// conflicting writes racing - and both sides genuinely need to learn about the
// other's exclusive tracks, which a one-sided pull would miss entirely.
public class LibrarySyncService
{
    private const int PageSize = 500;
    private const int MaxConcurrentAlbumFetches = 8;

    private readonly Library _library;
    private readonly string _ownFingerprint;
    private readonly string _ownAlias;

    public LibrarySyncService(Library library, string ownFingerprint, string ownAlias)
    {
        _library = library;
        _ownFingerprint = ownFingerprint;
        _ownAlias = ownAlias;
    }

    public async Task SyncWithAsync(DiscoveredDevice device)
    {
        if (string.IsNullOrEmpty(device.Fingerprint))
            return;

        var client = new OpenSubsonicClient(
            $"http://{device.EndPoint}",
            username: "", password: "",
            extraHeaders:
            [
                ("X-Flower-Fingerprint", _ownFingerprint),
                ("X-Flower-Alias", _ownAlias),
            ]);

        List<AlbumID3> albums;
        try
        {
            albums = await FetchAllAlbumsAsync(client);
        }
        catch
        {
            return; // Peer unreachable, not running this endpoint yet, or not (yet) trusted.
        }

        var placeholders = await FetchPlaceholdersAsync(client, albums, device.Fingerprint);
        if (placeholders.Count > 0)
            _library.MergeSyncedTracks(placeholders);
    }

    private static async Task<List<AlbumID3>> FetchAllAlbumsAsync(OpenSubsonicClient client)
    {
        var all = new List<AlbumID3>();
        var offset = 0;
        while (true)
        {
            var page = await client.GetAlbumList2Async(size: PageSize, offset: offset);
            all.AddRange(page);
            if (page.Count < PageSize)
                break;
            offset += PageSize;
        }

        return all;
    }

    // One request per album (getAlbumList2 alone doesn't carry per-song detail),
    // bounded to a modest concurrency so a library of hundreds of albums doesn't
    // serialize into hundreds of sequential LAN round-trips, without hammering
    // the peer's single HttpListener with everything at once either. A peer that
    // goes away mid-fetch just yields fewer placeholders this round - it
    // converges next time both devices are up.
    private static async Task<List<Track>> FetchPlaceholdersAsync(OpenSubsonicClient client, List<AlbumID3> albums, string peerFingerprint)
    {
        using var semaphore = new SemaphoreSlim(MaxConcurrentAlbumFetches);

        var albumResults = await Task.WhenAll(albums.Select(async album =>
        {
            await semaphore.WaitAsync();
            try
            {
                return await client.GetAlbumAsync(album.Id);
            }
            catch
            {
                return null;
            }
            finally
            {
                semaphore.Release();
            }
        }));

        return albumResults
            .Where(a => a?.Song != null)
            .SelectMany(a => a!.Song!)
            .Select(song => LibrarySyncMapper.ToPlaceholderTrack(song, peerFingerprint))
            .ToList();
    }
}
