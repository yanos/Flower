using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Security.Cryptography;
using System.Threading.Tasks;

using Avalonia.Media.Imaging;

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
public class AlbumArtLoader
{
    // The instance the ViewModels use. Set once by App.Bootstrap from the
    // container; the fallback covers heads and tests that never build one, and
    // behaves exactly like the old static class did with nothing registered
    // (no peer to fetch from, so remote art resolves to null).
    //
    // This is a seam, not a service locator: LoadAsync's dependencies are
    // constructor parameters that a test can supply, instead of two
    // Ioc.Default.GetService calls buried inside a static method that no test
    // could reach past. Threading the instance down to the ViewModels that
    // call it is docs/ARCHITECTURE-REVIEW.md 4.2's job - they are built by
    // static builders through `init` properties today.
    private static AlbumArtLoader? _current;
    public static AlbumArtLoader Current
    {
        get => _current ??= new AlbumArtLoader(null, null, AppLogging.CreateTypedLogger<AlbumArtLoader>());
        set => _current = value;
    }

    // Static because the pure helpers below (TryGetLocalArtBytes) are shared
    // with callers that have no instance - see AppLogging's own doc comment on
    // the loggers-for-non-DI-classes patterns it offers.
    private static readonly ILogger StaticLogger = AppLogging.CreateLogger("Flower.Services.AlbumArtLoader");

    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(10) };

    private readonly PeerTrackResolver? _peerResolver;
    private readonly DeviceIdentity? _deviceIdentity;
    private readonly ILogger _logger;

    // Both peer dependencies are nullable and unregistered on Flower.Web/WASM,
    // which has no P2P sync stack at all (see App.RegisterServices) - a null
    // either way means the same thing the old Ioc.Default.GetService returning
    // null meant: there is no peer to fetch remote art from.
    public AlbumArtLoader(PeerTrackResolver? peerResolver, DeviceIdentity? deviceIdentity, ILogger<AlbumArtLoader> logger)
    {
        _peerResolver = peerResolver;
        _deviceIdentity = deviceIdentity;
        _logger = logger;
    }

    // Disk cache for art fetched from a peer, content-addressed by
    // Track.OriginAlbumArtHash - see HandleGetCoverArtAsync/LibraryOpenSubsonicMapper
    // for where that hash comes from. Local (Path != null) tracks never use this;
    // reading straight off the file is already cheap and always current.
    private static string CacheDirectory => Path.Combine(AppDataDirectory.Path, "AlbumArtCache");

    // Key: directory path for a local track, or "remote:{hash}" for a synced one.
    // WeakReference so GC can reclaim bitmaps under memory pressure.
    private static readonly ConcurrentDictionary<string, WeakReference<Bitmap>> Cache = new();

    // The WeakReference cache above lets the GC reclaim art the moment nothing
    // is painting it, which for a scrolling album grid means re-decoding the
    // same covers over and over. This keeps the most recently used handful
    // strongly reachable so the visible set survives a collection, while the
    // weak cache still covers everything beyond it. Deliberately small: at
    // MaxArtPixels a bitmap is ~2.4 MB, so 32 entries is a ~75 MB ceiling in
    // the worst case, and this app runs on phones.
    private const int StrongCacheSize = 32;
    private static readonly object StrongCacheLock = new();
    private static readonly LinkedList<(string Key, Bitmap Bitmap)> StrongCache = new();

    // Also prunes cache entries whose bitmap has already been collected -
    // without this the dictionary only ever grows, one dead entry per album
    // ever displayed, for the life of the process.
    private void Retain(string key, Bitmap bitmap)
    {
        Cache[key] = new WeakReference<Bitmap>(bitmap);

        lock (StrongCacheLock)
        {
            for (var node = StrongCache.First; node != null; node = node.Next)
            {
                if (node.Value.Key == key)
                {
                    StrongCache.Remove(node);
                    break;
                }
            }

            StrongCache.AddFirst((key, bitmap));

            while (StrongCache.Count > StrongCacheSize)
            {
                // Evicted to weak-only, not disposed: a row or tile currently on
                // screen may still be painting this exact Bitmap.
                StrongCache.RemoveLast();
            }
        }

        foreach (var (deadKey, weak) in Cache)
        {
            if (!weak.TryGetTarget(out _))
                Cache.TryRemove(new KeyValuePair<string, WeakReference<Bitmap>>(deadKey, weak));
        }
    }

    // Refreshes LRU position on a hit, so the strong cache tracks what is
    // actually being displayed rather than what was displayed first.
    private bool TryGetCached(string key, out Bitmap bitmap)
    {
        if (Cache.TryGetValue(key, out var weak) && weak.TryGetTarget(out bitmap!))
        {
            Retain(key, bitmap);
            return true;
        }

        bitmap = null!;
        return false;
    }

    // Widest this app ever actually paints album art: mobile NowPlayingView's
    // 280pt square, allowing for a ~2.7x-DPI phone screen. Everything else is
    // smaller by a wide margin (TrackInfoWindow 200pt, AlbumTileControl 180pt,
    // a track row's art column 76pt at most - see TrackRowViewModel.ArtMaxSize).
    //
    // Embedded art in a modern library is routinely 1400x1400 or larger, which
    // is ~7.8 MB of decoded RGBA held per album; an album grid showing 40 tiles
    // could hold ~300 MB of bitmaps, and iOS jetsam does not wait for a GC to
    // reclaim them. One cache serves every one of those call sites, so the size
    // has to satisfy the largest. See docs/ARCHITECTURE-REVIEW.md Tier 1.2.
    private const int MaxArtPixels = 768;

    // Decodes at most MaxArtPixels wide, without ever scaling *up*.
    //
    // Avalonia's DecodeToWidth always scales to the width it's given, so
    // applying it unconditionally would inflate a small 300x300 cover into a
    // 768-wide bitmap - turning a 0.36 MB decode into a 2.36 MB one and making
    // exactly the problem this is here to fix worse for libraries with modest
    // art. There is no cheap way to read the intrinsic size first (SkiaSharp,
    // and so SKCodec, isn't a reference of this project - only Avalonia's own
    // Bitmap surface is available), so oversized art is decoded twice: once to
    // learn its size, once at the size actually wanted. The full-size decode is
    // transient and immediately dropped, which is the whole point - what used
    // to be *retained*, per album, for the life of the cache entry.
    private static Bitmap DecodeScaled(Stream stream)
    {
        var full = new Bitmap(stream);
        if (full.PixelSize.Width <= MaxArtPixels)
            return full;

        full.Dispose();
        stream.Position = 0;
        return Bitmap.DecodeToWidth(stream, MaxArtPixels);
    }

    public async Task<Bitmap?> LoadAsync(Track track)
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

    private async Task<Bitmap?> LoadLocalAsync(Track track)
    {
        var key = LocalCacheKey(track);

        if (TryGetCached(key, out var cached))
            return cached;

        var bmp = await Task.Run(() => LoadLocalBitmap(track));
        if (bmp != null)
            Retain(key, bmp);

        return bmp;
    }

    private Bitmap? LoadLocalBitmap(Track track)
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
            return DecodeScaled(ms);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not decode embedded album art for {Title} ({Path}); showing the placeholder icon instead",
                track.Title, track.Path);
            return null;
        }
    }

    // Raw art bytes for a track this device actually has a file for - see
    // LocalAlbumArtReader, which is the one implementation of the embedded-
    // tag-then-cover-file lookup and is shared with SyncHttpServer (serving
    // /rest/getCoverArt), LibraryOpenSubsonicMapper (hashing for CoverArt) and
    // Flower.Server. Callers that also need to know what to serve the bytes
    // *as* should use LocalAlbumArtReader.ForFile directly rather than sniff.
    public static byte[]? TryGetLocalArtBytes(Track track) =>
        LocalAlbumArtReader.ForFile(track.Path, StaticLogger)?.Bytes;

    // Fetches a placeholder track's album art from its origin peer, content-
    // addressed on disk by OriginAlbumArtHash so a restart (or the peer going
    // offline) doesn't mean re-fetching - and so an album's art changing on the
    // origin device is picked up automatically (new hash -> cache miss -> re-fetch)
    // without any separate invalidation logic.
    private async Task<Bitmap?> LoadRemoteAsync(Track track)
    {
        var hash = track.OriginAlbumArtHash;
        if (string.IsNullOrEmpty(hash) || string.IsNullOrEmpty(track.OriginDeviceFingerprint))
            return null;

        var cacheKey = $"remote:{hash}";
        if (TryGetCached(cacheKey, out var cached))
            return cached;

        var cachePath = Path.Combine(CacheDirectory, $"{hash}.art");
        if (File.Exists(cachePath))
        {
            var bmp = await Task.Run(() => TryDecodeFile(cachePath));
            if (bmp != null)
            {
                Retain(cacheKey, bmp);
                return bmp;
            }
        }

        // PeerTrackResolver is what actually decides whether track's origin peer is someone this
        // device may still talk to at all (only the currently paired Server -
        // see that class's own doc comment) - this call site doesn't need to
        // know that rule exists, just that null means "don't fetch."
        var peer = _peerResolver?.Resolve(track);
        if (peer == null || _deviceIdentity == null)
            return null;

        try
        {
            var albumId = LibraryOpenSubsonicMapper.AlbumIdFor(track);
            using var request = new HttpRequestMessage(HttpMethod.Get,
                $"http://{peer.EndPoint}/rest/getCoverArt?id={Uri.EscapeDataString(albumId)}");
            request.Headers.Add("X-Flower-Fingerprint", _deviceIdentity.Fingerprint);
            request.Headers.Add("X-Flower-Alias", _deviceIdentity.Alias);
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

            Retain(cacheKey, bmp);
            return bmp;
        }
        catch (Exception ex)
        {
            // Debug, not Warning - peer unreachable/offline or not (yet) trusted
            // is routine, not a real error (SyncHttpServer/NetworkDiscoveryService
            // log the actual trust/reachability decisions themselves already).
            _logger.LogDebug(ex, "Could not fetch remote album art for {Album} from {Fingerprint}; showing the placeholder icon instead",
                track.Album, track.OriginDeviceFingerprint);
            return null;
        }
    }

    private Bitmap? TryDecodeBytes(byte[] bytes)
    {
        try
        {
            using var ms = new MemoryStream(bytes);
            return DecodeScaled(ms);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not decode {ByteCount} bytes of downloaded remote album art; showing the placeholder icon instead", bytes.Length);
            return null;
        }
    }

    private Bitmap? TryDecodeFile(string path)
    {
        try
        {
            using var stream = File.OpenRead(path);
            return DecodeScaled(stream);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not decode cached remote album art at {Path}; showing the placeholder icon instead", path);
            return null;
        }
    }

    // Shared with LibraryOpenSubsonicMapper, which stamps this same hash onto
    // CoverArt server-side - one hashing implementation, not two that could drift.
    public static string ComputeArtHash(byte[] bytes) =>
        Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
}
