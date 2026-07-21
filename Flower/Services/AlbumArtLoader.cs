using System;
using System.Collections.Concurrent;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Security.Cryptography;
using System.Threading.Tasks;

using Avalonia.Media.Imaging;

using CommunityToolkit.Mvvm.DependencyInjection;

using Microsoft.Extensions.Logging;

using Flower.Logging;
using Flower.Models;
using Flower.Persistence;

namespace Flower.Services;

// Shared album-art lookup (embedded tag picture, falling back to a cover/folder
// image file) used by both TrackRowViewModel (track list art column) and
// TrackInfoWindow (header thumbnail) - extracted here so there's one cache and
// one implementation instead of two copies drifting apart. Also handles art for
// a placeholder track known via library sync (Path == null, no local file to
// read) by fetching it from the origin peer - see SYNC-PLAN.md Phase 3.
public static class AlbumArtLoader
{
    // A static class (called directly, not DI-resolved) uses AppLogging.CreateLogger
    // rather than constructor injection - see AppLogging's own doc comment on the
    // two loggers-for-non-DI-classes patterns it offers.
    private static readonly ILogger Logger = AppLogging.CreateLogger("Flower.Services.AlbumArtLoader");

    private static readonly string[] ImageExtensions =
        [".jpg", ".jpeg", ".png", ".webp", ".bmp", ".gif", ".tiff", ".tif"];

    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(10) };

    // Disk cache for art fetched from a peer, content-addressed by
    // Track.OriginAlbumArtHash - see HandleGetCoverArtAsync/LibraryOpenSubsonicMapper
    // for where that hash comes from. Local (Path != null) tracks never use this;
    // reading straight off the file is already cheap and always current.
    private static string CacheDirectory => Path.Combine(AppDataDirectory.Path, "AlbumArtCache");

    // Key: directory path for a local track, or "remote:{hash}" for a synced one.
    // WeakReference so GC can reclaim bitmaps under memory pressure.
    private static readonly ConcurrentDictionary<string, WeakReference<Bitmap>> Cache = new();

    public static async Task<Bitmap?> LoadAsync(Track track)
    {
        if (track.Path != null)
            return await LoadLocalAsync(track);

        return await LoadRemoteAsync(track);
    }

    // Album/EffectiveAlbumArtist, not directory - a normally-organized local
    // library happens to have one directory per album, but a *downloaded*
    // track (LibraryDownloadService) never does: every downloaded file lands
    // in one shared flat folder per platform (all of "Downloads", or the
    // Documents root on iOS), regardless of which album it's actually from.
    // Confirmed on a real device: once one downloaded track's art got cached
    // under that shared directory key, every other downloaded track sharing
    // the same folder returned that same wrong bitmap - visible in practice
    // as Recently Added's first tile always matching whatever was most
    // recently downloaded instead of its own album's actual art. Falls back
    // to directory only for the rare case of a blank Album tag, where there's
    // nothing better to key on.
    private static string LocalCacheKey(Track track) =>
        !string.IsNullOrEmpty(track.Album)
            ? $"album:{track.Album}|{track.EffectiveAlbumArtist}"
            : $"dir:{Path.GetDirectoryName(track.Path ?? "") ?? ""}";

    private static async Task<Bitmap?> LoadLocalAsync(Track track)
    {
        var key = LocalCacheKey(track);

        if (Cache.TryGetValue(key, out var weak) && weak.TryGetTarget(out var cached))
            return cached;

        var bmp = await Task.Run(() => LoadLocalBitmap(track));
        if (bmp != null)
            Cache[key] = new WeakReference<Bitmap>(bmp);

        return bmp;
    }

    private static Bitmap? LoadLocalBitmap(Track track)
    {
        var data = TryGetLocalArtBytes(track);
        if (data == null)
            return null;

        // Unlike TryDecodeBytes/TryDecodeFile below (the remote-art paths), this
        // one wasn't guarded - a track whose embedded picture data Skia can't
        // decode (corrupt, truncated, or an unsupported encoding like a CMYK
        // JPEG) threw ArgumentException straight out of Task.Run in
        // LoadLocalAsync, an unobserved fault rather than just falling back to
        // the placeholder icon like every other art-miss in this file does.
        try
        {
            using var ms = new MemoryStream(data);
            return new Bitmap(ms);
        }
        catch (Exception ex)
        {
            Logger.LogWarning(ex, "Could not decode embedded album art for {Title} ({Path}); showing the placeholder icon instead",
                track.Title, track.Path);
            return null;
        }
    }

    // Raw art bytes for a track this device actually has a file for - embedded
    // tag picture first, falling back to a cover.*/folder.* file in the same
    // directory. Shared with LibraryOpenSubsonicMapper (to hash for CoverArt) and
    // SyncHttpServer (to serve /rest/getCoverArt), not just this loader's own
    // Bitmap decoding, so all three agree on exactly what "this album's art" means.
    public static byte[]? TryGetLocalArtBytes(Track track)
    {
        if (track.Path == null)
            return null;

        // 1. Embedded tag art
        try
        {
            using var tagFile = TagLib.File.Create(track.Path);
            var pic = tagFile.Tag.Pictures.FirstOrDefault();
            if (pic?.Data?.Data is { Length: > 0 } data)
                return data;
        }
        catch (Exception ex)
        {
            // Debug, not Warning - TagLib failing to open a file's tags entirely
            // is routine for oddball/corrupt files scattered through a large real
            // library, not something worth a warning on its own for every one.
            Logger.LogDebug(ex, "Could not read embedded art tag for {Path}", track.Path);
        }

        // 2. cover.*/folder.* in the same directory
        try
        {
            var dir = Path.GetDirectoryName(track.Path);
            if (dir != null)
            {
                var file = Directory.EnumerateFiles(dir).FirstOrDefault(f =>
                {
                    var stem = Path.GetFileNameWithoutExtension(f);
                    var ext  = Path.GetExtension(f).ToLowerInvariant();
                    return (stem.Equals("cover",  StringComparison.OrdinalIgnoreCase) ||
                            stem.Equals("folder", StringComparison.OrdinalIgnoreCase))
                        && ImageExtensions.Contains(ext);
                });
                if (file != null)
                    return File.ReadAllBytes(file);
            }
        }
        catch (Exception ex)
        {
            Logger.LogDebug(ex, "Could not read a cover/folder image next to {Path}", track.Path);
        }

        return null;
    }

    // Fetches a placeholder track's album art from its origin peer, content-
    // addressed on disk by OriginAlbumArtHash so a restart (or the peer going
    // offline) doesn't mean re-fetching - and so an album's art changing on the
    // origin device is picked up automatically (new hash -> cache miss -> re-fetch)
    // without any separate invalidation logic.
    private static async Task<Bitmap?> LoadRemoteAsync(Track track)
    {
        var hash = track.OriginAlbumArtHash;
        if (string.IsNullOrEmpty(hash) || string.IsNullOrEmpty(track.OriginDeviceFingerprint))
            return null;

        var cacheKey = $"remote:{hash}";
        if (Cache.TryGetValue(cacheKey, out var weak) && weak.TryGetTarget(out var cached))
            return cached;

        var cachePath = Path.Combine(CacheDirectory, $"{hash}.art");
        if (File.Exists(cachePath))
        {
            var bmp = await Task.Run(() => TryDecodeFile(cachePath));
            if (bmp != null)
            {
                Cache[cacheKey] = new WeakReference<Bitmap>(bmp);
                return bmp;
            }
        }

        // Ioc.Default is used as a service locator elsewhere in this codebase
        // (Views/Controls resolving their ViewModels) - the same pattern here
        // keeps LoadAsync(track) a simple static call for its three existing
        // callers rather than threading a peer-resolution dependency through
        // TrackRowViewModel/AlbumTileViewModel/TrackInfoWindow.
        var networkDiscovery = Ioc.Default.GetService<NetworkDiscoveryService>();
        var deviceIdentity = Ioc.Default.GetService<DeviceIdentity>();
        var peer = networkDiscovery?.FindByFingerprint(track.OriginDeviceFingerprint);
        if (peer == null || deviceIdentity == null)
            return null;

        try
        {
            var albumId = LibraryOpenSubsonicMapper.AlbumId(track.Album, track.Artists);
            using var request = new HttpRequestMessage(HttpMethod.Get,
                $"http://{peer.EndPoint}/rest/getCoverArt?id={Uri.EscapeDataString(albumId)}");
            request.Headers.Add("X-Flower-Fingerprint", deviceIdentity.Fingerprint);
            request.Headers.Add("X-Flower-Alias", deviceIdentity.Alias);
            request.Headers.ConnectionClose = true;

            using var response = await Http.SendAsync(request);
            if (!response.IsSuccessStatusCode)
                return null;

            var bytes = await response.Content.ReadAsByteArrayAsync();

            Directory.CreateDirectory(CacheDirectory);
            await File.WriteAllBytesAsync(cachePath, bytes);

            // Decode off the UI thread - same reason LoadLocalAsync/the cached-file
            // path above both use Task.Run: this runs on whatever thread called
            // LoadAsync (typically the UI thread, via TrackRowViewModel.AlbumArt's
            // getter), and decoding a full-size image inline there stalls scrolling
            // every time a placeholder row's art finishes downloading.
            var bmp = await Task.Run(() => TryDecodeBytes(bytes));
            if (bmp == null)
                return null;

            Cache[cacheKey] = new WeakReference<Bitmap>(bmp);
            return bmp;
        }
        catch (Exception ex)
        {
            // Debug, not Warning - peer unreachable/offline or not (yet) trusted
            // is routine, not a real error (SyncHttpServer/NetworkDiscoveryService
            // log the actual trust/reachability decisions themselves already).
            Logger.LogDebug(ex, "Could not fetch remote album art for {Album} from {Fingerprint}; showing the placeholder icon instead",
                track.Album, track.OriginDeviceFingerprint);
            return null;
        }
    }

    private static Bitmap? TryDecodeBytes(byte[] bytes)
    {
        try
        {
            using var ms = new MemoryStream(bytes);
            return new Bitmap(ms);
        }
        catch (Exception ex)
        {
            Logger.LogWarning(ex, "Could not decode {ByteCount} bytes of downloaded remote album art; showing the placeholder icon instead", bytes.Length);
            return null;
        }
    }

    private static Bitmap? TryDecodeFile(string path)
    {
        try
        {
            return new Bitmap(path);
        }
        catch (Exception ex)
        {
            Logger.LogWarning(ex, "Could not decode cached remote album art at {Path}; showing the placeholder icon instead", path);
            return null;
        }
    }

    // Shared with LibraryOpenSubsonicMapper, which stamps this same hash onto
    // CoverArt server-side - one hashing implementation, not two that could drift.
    public static string ComputeArtHash(byte[] bytes) =>
        Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
}
