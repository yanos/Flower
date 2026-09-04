using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using System.Text.Json;

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

    // Through PeerHttpClient rather than `new HttpClient()` so cover art is
    // fetched over the same accepted-certificate rule as everything else - this
    // client talks to the same peer the catalog came from, on the same origin.
    private static readonly HttpClient Http = PeerHttpClient.Create(TimeSpan.FromSeconds(10));

    private readonly ICoverArtUrlResolver? _artUrls;
    private readonly IPeerCredentials? _credentials;
    private readonly ILogger _logger;

    // Both remote dependencies stay nullable: a head that has neither is a head
    // with no origin to fetch art from, which is what the old Ioc.Default.
    // GetService returning null meant too. What changed is that the browser is
    // no longer one of them - it registers an OriginCoverArtUrlResolver and a
    // BrowserPeerCredentials, so remote art works there as well as on the
    // desktop (see App.RegisterBrowserServices). The nulls now cover only tests
    // and the Current fallback below.
    public AlbumArtLoader(ICoverArtUrlResolver? artUrls, IPeerCredentials? credentials, ILogger<AlbumArtLoader> logger)
    {
        _artUrls = artUrls;
        _credentials = credentials;
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

    // Virtual for the same reason PeerTrackResolver.Resolve is: it is the one
    // seam a test needs to count how often a row actually asks for its art
    // (see TrackRowMergeTests) without standing up real files and a decoder.
    public virtual async Task<Bitmap?> LoadAsync(Track track)
    {
        if (IsLocalFile(track))
            return await LoadLocalAsync(track);

        return await LoadRemoteAsync(track);
    }

    // A Path is not automatically a file. PlaylistControlViewModel.
    // ResolveForPlaybackAsync clones a placeholder and puts the minted stream
    // URL in Path, so the *currently playing* track on a browser tab - or on a
    // desktop streaming from a peer - has a Path that no filesystem read can
    // ever satisfy. Its art is on the origin, exactly as it is for the
    // placeholder it was cloned from. TrackDecoder.EnsureMedia and
    // CurrentlyPlayingControlViewModel use the same "://" test.
    public static bool IsLocalFile(Track track) =>
        track.Path is { } path && !path.Contains("://");

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
            // Debug, not Warning: this runs once per rendered row, so a library
            // with a handful of odd art tags turns a scroll through the album
            // grid into a wall of warnings for something the app handles by
            // design - the placeholder icon is the intended outcome here, not a
            // degraded one.
            _logger.LogDebug(ex, "Could not decode embedded album art for {Title} ({Path}); showing the placeholder icon instead",
                track.Title, track.Path);
            return null;
        }
    }

    // Raw art bytes for a track this device actually has a file for - see
    // LocalAlbumArtReader, which is the one implementation of the embedded-
    // tag-then-cover-file lookup and is shared with LibraryOpenSubsonicMapper
    // (hashing for CoverArt) and Flower.Server (serving /rest/getCoverArt).
    // Callers that also need to know what to serve the bytes *as* should use
    // LocalAlbumArtReader.ForFile directly rather than sniff.
    public static byte[]? TryGetLocalArtBytes(Track track) =>
        LocalAlbumArtReader.ForFile(track.Path, StaticLogger)?.Bytes;

    // The undecoded, un-downscaled art for either kind of track - what Track
    // Info's Artwork tab reports the real dimensions of and opens at full size,
    // as opposed to the MaxArtPixels bitmap LoadAsync hands back for display. A
    // placeholder track's answer is whatever LoadRemoteAsync already wrote into
    // the content-addressed disk cache, so this never issues a fetch of its
    // own: no cached copy means no bytes, which is the same "nothing to show"
    // the caller handles anyway. That path also has no MIME type to report -
    // the cache is keyed by content hash and stores bytes alone - hence the
    // empty string rather than a guess sniffed back out of the bytes.
    public static LocalAlbumArt? TryGetArt(Track track)
    {
        if (IsLocalFile(track))
            return LocalAlbumArtReader.ForFile(track.Path, StaticLogger);

        if (track.OriginAlbumArtHash is not { Length: > 0 } hash)
            return null;

        var cachePath = Path.Combine(CacheDirectory, $"{hash}.art");
        try
        {
            if (!File.Exists(cachePath))
                return null;

            // Sniffed, because the cache stores bytes and nothing else - see
            // this method's own note above. Empty, not a guess, when the magic
            // number matches nothing known.
            var bytes = File.ReadAllBytes(cachePath);
            return new LocalAlbumArt(bytes, LocalAlbumArtReader.MimeTypeForBytes(bytes) ?? "");
        }
        catch (Exception ex)
        {
            StaticLogger.LogDebug(ex, "Could not read cached remote art at {Path}", cachePath);
            return null;
        }
    }

    // Bumped by Invalidate below. Views that have already published a
    // bitmap poll this rather than subscribe to an event: a rewritten cover
    // leaves the Track instance untouched, so nothing in the row-merge path
    // (TrackRowViewModel.ArtSourceMatches) can notice, and a library-sized list
    // of rows attaching handlers to a static event is a worse trade than an int
    // compare on the paint path. Deliberately not per-album - an invalidation
    // is rare (a user replacing artwork), and the cost of the over-broad answer
    // is one re-read of a file that is almost certainly still in the OS cache.
    public static int CacheGeneration => Volatile.Read(ref _cacheGeneration);
    private static int _cacheGeneration;

    // Drops a track's album whose art was just rewritten (Track Info's Artwork
    // tab) out of the caches, so the next LoadAsync fetches it again instead of
    // handing back the bitmap decoded from the old bytes. Keyed the same way
    // LoadLocalAsync/LoadRemoteAsync key it, hence the shared LocalCacheKey
    // rather than a second copy of that rule.
    //
    // The remote half is not symmetric with the local one, and has to do more.
    // A local file is re-read on the next miss and is current by definition,
    // but synced art is content-addressed on disk by OriginAlbumArtHash - and
    // against Flower.Server that "hash" is the album id (SubsonicMapper's
    // CoverArt field), which does not change when the art behind it does. So
    // the cache file has to be deleted outright; leaving it would mean the next
    // load finds the old picture under the same key and never asks the server.
    //
    // The evicted Bitmap is deliberately not disposed: rows and tiles currently
    // on screen may still be painting it, exactly as in Retain's own eviction.
    public static void Invalidate(Track track)
    {
        Interlocked.Increment(ref _cacheGeneration);

        Forget(LocalCacheKey(track));

        if (track.OriginAlbumArtHash is { Length: > 0 } hash)
        {
            Forget($"remote:{hash}");
            var cachePath = Path.Combine(CacheDirectory, $"{hash}.art");
            try
            {
                File.Delete(cachePath);
            }
            catch (Exception ex)
            {
                // The picture is already changed at the origin; a stale cache
                // file only means this device keeps painting the old one until
                // something else clears it, which is not worth failing a write
                // that has already happened.
                StaticLogger.LogDebug(ex, "Could not remove the cached remote art at {Path} after replacing it", cachePath);
            }
        }
    }

    private static void Forget(string key)
    {
        Cache.TryRemove(key, out _);

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
        }
    }

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

            // Nothing on disk under this hash is ever going to become decodable
            // - the file is truncated, or it predates the decode-first rule
            // below and is actually a Subsonic error envelope. Drop it so the
            // fetch beneath re-fills it, instead of warning about the same
            // unreadable file on every load for the life of the cache.
            DiscardCacheFile(cachePath);
        }

        // ICoverArtUrlResolver is what actually decides where - and whether -
        // this track's art can be asked for: against a peer that rule is "only
        // the currently paired Server" (see PeerTrackResolver), and in a browser
        // it is simply the origin. This call site doesn't need to know either
        // rule exists, just that null means "don't fetch."
        if (_artUrls == null || _credentials == null)
            return null;

        var url = _artUrls.Resolve(track);
        if (url == null)
            return null;

        try
        {
            var bytes = await FetchArtBytesAsync(track, url);
            if (bytes == null || bytes.Length == 0)
                return null;

            // Decode off the UI thread - same reason LoadLocalAsync/the cached-file
            // path above both use Task.Run: this runs on whatever thread called
            // LoadAsync (typically the UI thread, via TrackRowViewModel.AlbumArt's
            // getter), and decoding a full-size image inline there stalls scrolling
            // every time a placeholder row's art finishes downloading.
            var bmp = await Task.Run(() => TryDecodeBytes(bytes));
            if (bmp == null)
            {
                // Reached the peer and got *something* back that is not an
                // image - a Subsonic error envelope, most likely. Caching it
                // would turn one transient refusal into a permanently broken
                // tile, since the cache-file branch above hits before the
                // fetch. Hence decode first, write second.
                _logger.LogDebug("Remote album art for {Album} from {Fingerprint} was not a decodable image ({ByteCount} bytes); not caching it",
                    track.Album, track.OriginDeviceFingerprint, bytes.Length);
                return null;
            }

            Directory.CreateDirectory(CacheDirectory);
            await File.WriteAllBytesAsync(cachePath, bytes);

            Retain(cacheKey, bmp);
            return bmp;
        }
        catch (Exception ex)
        {
            // Debug, not Warning - peer unreachable/offline or not (yet) trusted
            // is routine, not a real error (NetworkDiscoveryService and the sync
            // services log the actual trust/reachability decisions already).
            _logger.LogDebug(ex, "Could not fetch remote album art for {Album} from {Fingerprint}; showing the placeholder icon instead",
                track.Album, track.OriginDeviceFingerprint);
            return null;
        }
    }

    // How long a tile waits for its neighbours before the request goes out.
    // Short enough that a stationary grid does not visibly hesitate, long
    // enough that a scroll's worth of tiles - which arrive within a frame or
    // two of each other - land in the same request.
    private const int BatchDebounceMs = 40;

    private readonly Lock _batchLock = new();
    private readonly Dictionary<string, PendingArt> _waiting = new(StringComparer.Ordinal);
    private bool _batchRunning;

    // One album's art, and everything needed to ask for it either way: the
    // batch id, the single-request URL that is the fallback, and the callers
    // waiting on the answer. Several tracks from the same album collapse onto
    // one of these, which is most of what the batching buys on a track list.
    private sealed record PendingArt(string Endpoint, string Id, string Url, TaskCompletionSource<byte[]?> Completion);

    // The bytes for one track's art, asked for in company where that is
    // possible. Returns null for "no art", which is a real answer and not an
    // error: plenty of albums have no picture.
    private async Task<byte[]?> FetchArtBytesAsync(Track track, string url)
    {
        if (_artUrls!.ResolveBatch(track) is { } batch)
            return await JoinBatchAsync(batch.Endpoint, batch.Id, url);

        return await FetchOneAsync(url);
    }

    private Task<byte[]?> JoinBatchAsync(string endpoint, string id, string url)
    {
        PendingArt pending;
        var start = false;

        lock (_batchLock)
        {
            if (!_waiting.TryGetValue(id, out pending!))
            {
                pending = new PendingArt(endpoint, id, url,
                    new TaskCompletionSource<byte[]?>(TaskCreationOptions.RunContinuationsAsynchronously));
                _waiting[id] = pending;
            }

            if (!_batchRunning)
            {
                _batchRunning = true;
                start = true;
            }
        }

        if (start)
            _ = Task.Run(DrainBatchesAsync);

        return pending.Completion.Task;
    }

    // Keeps draining for as long as tiles keep arriving, so a long scroll is a
    // steady handful of requests rather than one per debounce window that
    // happens to be empty. Stops as soon as a window passes with nothing
    // waiting; the next tile starts it again.
    private async Task DrainBatchesAsync()
    {
        while (true)
        {
            await Task.Delay(BatchDebounceMs);

            List<PendingArt> batch;
            lock (_batchLock)
            {
                if (_waiting.Count == 0)
                {
                    _batchRunning = false;
                    return;
                }

                // Grouped by endpoint, because the paired server can change
                // between one tile and the next and a batch is addressed to
                // one server. Taking only the first endpoint's entries leaves
                // the others for the next pass rather than mixing them.
                var endpoint = _waiting.Values.First().Endpoint;
                batch = _waiting.Values
                    .Where(entry => entry.Endpoint == endpoint)
                    .Take(CoverArtBatch.MaxIds)
                    .ToList();

                foreach (var entry in batch)
                    _waiting.Remove(entry.Id);
            }

            await SendBatchAsync(batch);
        }
    }

    private async Task SendBatchAsync(List<PendingArt> batch)
    {
        try
        {
            var payload = JsonSerializer.SerializeToUtf8Bytes(
                new CoverArtBatchRequest { Ids = batch.Select(entry => entry.Id).ToList() });

            using var request = new HttpRequestMessage(HttpMethod.Post, batch[0].Endpoint)
            {
                Content = new ByteArrayContent(payload),
            };
            request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");

            // Signed over the body, like every other POST into a peer. The
            // signature covers the id list, so a batch cannot be rewritten in
            // flight into a request for something else.
            await request.AddPeerCredentialsAsync(_credentials!, payload);
            if (_artUrls!.ClosesConnection)
                request.Headers.ConnectionClose = true;

            using var response = await Http.SendAsync(request);
            if (!response.IsSuccessStatusCode)
            {
                await FallBackToSingleAsync(batch, $"the server answered {(int)response.StatusCode}");
                return;
            }

            var frame = await response.Content.ReadAsByteArrayAsync();
            var art = CoverArtBatch.Read(frame);
            if (art == null)
            {
                await FallBackToSingleAsync(batch, $"the {frame.Length}-byte response was not a readable batch frame");
                return;
            }

            foreach (var entry in batch)
            {
                // An id the response left out is not an id with no art - the
                // server truncates at its byte cap - so it goes back through
                // the single-request path rather than being answered "none".
                // An id that came back with zero bytes *is* an answer.
                if (art.TryGetValue(entry.Id, out var bytes))
                    entry.Completion.TrySetResult(bytes.Length == 0 ? null : bytes);
                else
                    entry.Completion.TrySetResult(await FetchOneAsync(entry.Url));
            }
        }
        catch (Exception ex)
        {
            await FallBackToSingleAsync(batch, ex.Message);
        }
    }

    // One request per album, which is what this code did before the batch
    // existed - so a server that has no batch door, or one that answered
    // something unreadable, degrades to the old behaviour rather than to blank
    // tiles.
    private async Task FallBackToSingleAsync(List<PendingArt> batch, string reason)
    {
        _logger.LogDebug("Batched album art for {Count} albums did not work out ({Reason}); asking one at a time instead",
            batch.Count, reason);

        foreach (var entry in batch)
            entry.Completion.TrySetResult(await FetchOneAsync(entry.Url));
    }

    private async Task<byte[]?> FetchOneAsync(string url)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            // Signed, like every other call into a peer's /rest surface. This
            // used to send a bare fingerprint and alias with no signature at
            // all, which the app's own listener tolerated but Flower.Server
            // does not - and its refusal is a *Subsonic* refusal, so it comes
            // back as HTTP 200 carrying an error envelope. Two things followed
            // from that, both of them bad: the JSON error body was written
            // straight into the art cache as if it were an image (see
            // LoadRemoteAsync's decode-before-cache rule), and each refusal
            // charged the server's FailedAuthLimiter, so ten album tiles were
            // enough to 429 the entire /rest surface for a minute - including
            // /rest/stream, which is why playback of server-hosted tracks died
            // wholesale.
            await request.AddPeerCredentialsAsync(_credentials!);
            if (_artUrls!.ClosesConnection)
                request.Headers.ConnectionClose = true;

            using var response = await Http.SendAsync(request);
            if (!response.IsSuccessStatusCode)
                return null;

            return await response.Content.ReadAsByteArrayAsync();
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Could not fetch album art from {Url}; showing the placeholder icon instead", url);
            return null;
        }
    }

    // The request shape SyncEndpoints reads. Declared here rather than shared
    // with the server's own copy because it is two lines and a shared DTO
    // would drag the whole sync JSON context across for them.
    private sealed class CoverArtBatchRequest
    {
        public List<string> Ids { get; set; } = [];
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
            _logger.LogDebug(ex, "Could not decode {ByteCount} bytes of downloaded remote album art; showing the placeholder icon instead", bytes.Length);
            return null;
        }
    }

    private void DiscardCacheFile(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception ex)
        {
            // Losing the race with another head deleting the same file, or a
            // read-only cache directory: the art still falls back to the
            // placeholder either way, so this is not worth failing the load for.
            _logger.LogDebug(ex, "Could not remove undecodable cached album art at {Path}", path);
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
            _logger.LogDebug(ex, "Could not decode cached remote album art at {Path}; discarding it and re-fetching", path);
            return null;
        }
    }

    // Shared with LibraryOpenSubsonicMapper, which stamps this same hash onto
    // CoverArt server-side - one hashing implementation, not two that could drift.
    public static string ComputeArtHash(byte[] bytes) =>
        Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
}
