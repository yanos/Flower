using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using System.Text.Json;

using Avalonia;
using Avalonia.Media.Imaging;
using Avalonia.Threading;

using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

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
        get => _current ??= new AlbumArtLoader(null, null, NullLogger<AlbumArtLoader>.Instance);
        set => _current = value;
    }

    // For the static helpers below (TryGetLocalArtBytes), which are shared with
    // callers that have no instance: the logger of the instance App installs
    // from the container, rather than a static one of their own.
    private static ILogger StaticLogger => Current._logger;

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

    // Disk cache for art fetched from a peer, keyed by Track.OriginAlbumArtId
    // - the origin's album id, one entry per album rather than one per version
    // of its cover; see that field's own comment for why it identifies the album
    // and not the bytes. Local (Path != null) tracks never use this; reading
    // straight off the file is already cheap and always current.
    private static string CacheDirectory => Path.Combine(AppDataDirectory.Path, "AlbumArtCache");

    // Key: directory path for a local track, or "remote:{albumId}" for a synced one.
    // WeakReference so GC can reclaim bitmaps under memory pressure.
    private static readonly ConcurrentDictionary<string, WeakReference<Bitmap>> Cache = new();

    // Albums the origin said it has no picture for, by origin album id, so
    // the next look at one is not another request. A library's bare albums
    // are the ones a playlist collage walks past looking for four covers, and
    // asking again for every one of them each time a collage was rebuilt is
    // what spent a phone's whole art budget in a minute - after which the
    // cover just edited was refused like the rest. Until Invalidate, which a
    // cover added on any device reaches, and for this run of the app only.
    private static readonly ConcurrentDictionary<string, byte> KnownBare = new(StringComparer.Ordinal);

    // The WeakReference cache above lets the GC reclaim art the moment nothing
    // is painting it, which for a scrolling album grid means re-decoding the
    // same covers over and over. This keeps the most recently used handful
    // strongly reachable so the visible set survives a collection, while the
    // weak cache still covers everything beyond it. Deliberately small: at
    // MaxArtPixels a bitmap is ~2.4 MB, so 32 entries is a ~75 MB ceiling in
    // the worst case, and this app runs on phones.
    //
    // Thumbnails (LoadThumbnailAsync) get a cache of their own, because they
    // are the opposite shape: small, and wanted by the hundred. A song list is
    // mostly one album per row, so a fling through it wants a new cover every
    // few frames, and 32 entries shared with the album grid meant almost every
    // row that came back on screen decoded its cover again. At ThumbnailPixels
    // 256 of them are ~37 MB at most.
    private const int StrongCacheSize = 32;
    private const int StrongThumbnailCacheSize = 256;
    private static readonly StrongLru StrongCache = new(StrongCacheSize);
    private static readonly StrongLru StrongThumbnailCache = new(StrongThumbnailCacheSize);

    private sealed class StrongLru(int capacity)
    {
        private readonly object _lock = new();
        private readonly LinkedList<(string Key, Bitmap Bitmap)> _entries = new();

        // Each entry's node, so a hit moves it to the front without walking
        // the list to find it. The walk was every thumbnail hit's cost during
        // a fling: up to 256 nodes, on the UI thread, per row scrolled in.
        private readonly Dictionary<string, LinkedListNode<(string Key, Bitmap Bitmap)>> _nodes = new();

        public void Retain(string key, Bitmap bitmap)
        {
            lock (_lock)
            {
                if (_nodes.TryGetValue(key, out var node))
                {
                    _entries.Remove(node);
                    node.Value = (key, bitmap);
                    _entries.AddFirst(node);
                    return;
                }

                _nodes[key] = _entries.AddFirst((key, bitmap));
                while (_entries.Count > capacity)
                {
                    // Evicted to weak-only, not disposed: a row or tile currently on
                    // screen may still be painting this exact Bitmap.
                    _nodes.Remove(_entries.Last!.Value.Key);
                    _entries.RemoveLast();
                }
            }
        }

        public void Forget(string key)
        {
            lock (_lock)
            {
                if (_nodes.Remove(key, out var node))
                    _entries.Remove(node);
            }
        }
    }

    private enum ArtSize
    {
        Full,
        Thumbnail,
    }

    // Thumbnails are cached beside full-size art under the same key with this
    // in front, so one album's two sizes never stand in for each other.
    private const string ThumbnailKeyPrefix = "thumb|";

    private static string Keyed(string key, ArtSize size) =>
        size == ArtSize.Thumbnail ? ThumbnailKeyPrefix + key : key;

    // Also prunes cache entries whose bitmap has already been collected -
    // without this the dictionary only ever grows, one dead entry per album
    // ever displayed, for the life of the process. Every half a minute at
    // most, rather than on every call: the sweep walks every album ever shown,
    // and it used to run on every cache hit as well as every load.
    private void Retain(string key, Bitmap bitmap)
    {
        Cache[key] = new WeakReference<Bitmap>(bitmap);
        StrongCacheFor(key).Retain(key, bitmap);

        var now = Environment.TickCount64;
        var due = Volatile.Read(ref _nextSweepAt);
        if (now < due || Interlocked.CompareExchange(ref _nextSweepAt, now + SweepIntervalMs, due) != due)
            return;

        foreach (var (deadKey, weak) in Cache)
        {
            if (!weak.TryGetTarget(out _))
                Cache.TryRemove(new KeyValuePair<string, WeakReference<Bitmap>>(deadKey, weak));
        }
    }

    private const long SweepIntervalMs = 30_000;
    private static long _nextSweepAt;

    private static StrongLru StrongCacheFor(string key) =>
        key.StartsWith(ThumbnailKeyPrefix, StringComparison.Ordinal) ? StrongThumbnailCache : StrongCache;

    // Refreshes LRU position on a hit, so the strong cache tracks what is
    // actually being displayed rather than what was displayed first. The weak
    // entry already holds this bitmap, so it is left as it is.
    private bool TryGetCached(string key, out Bitmap bitmap)
    {
        if (Cache.TryGetValue(key, out var weak) && weak.TryGetTarget(out bitmap!))
        {
            StrongCacheFor(key).Retain(key, bitmap);
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
    // art. So the intrinsic size is read first, from the header (ImageHeader),
    // and the picture decoded once at the size wanted. A picture whose header
    // that cannot read is decoded twice, as all of them used to be: once at
    // full size to learn it, then again at the size wanted - a ~7.8 MB decode
    // thrown away per 1400px cover, and the dearest thing a cold scroll did.
    // The width a track row's thumbnail is decoded at: the largest any row
    // paints it is the desktop list's 76pt art column (TrackRowViewModel.
    // ArtMaxSize) on a 2x screen, 152px, and a phone row's 52pt on a 3x one is
    // 156px - so 192 is sharp at both with a little to spare. A sixteenth of the
    // pixels of MaxArtPixels, and a texture the GPU takes as one small upload
    // rather than 2.4 MB each time a row scrolls in.
    private const int ThumbnailPixels = 192;

    private static Bitmap Decode(Stream stream, ArtSize size) =>
        size == ArtSize.Thumbnail ? DecodeThumbnail(stream) : DecodeScaled(stream);

    // One decode, straight to the width wanted - a JPEG decoder can do most of
    // that downscale inside the decode itself, so this is cheaper than reading
    // the picture at full size first. Art narrower than ThumbnailPixels is
    // scaled up to it, which for a bitmap this small is not worth a second
    // decode to avoid.
    private static Bitmap DecodeThumbnail(Stream stream) =>
        Bitmap.DecodeToWidth(stream, ThumbnailPixels);

    private static Bitmap DecodeScaled(Stream stream)
    {
        if (ImageHeader.TryReadSize(stream) is { } size)
            return size.Width <= MaxArtPixels ? new Bitmap(stream) : Bitmap.DecodeToWidth(stream, MaxArtPixels);

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
    public virtual Task<Bitmap?> LoadAsync(Track track) => LoadAsync(track, ArtSize.Full);

    /// <summary>
    /// The track's art at ThumbnailPixels wide, for the places that draw it at
    /// the size of a list row - see ThumbnailPixels and StrongThumbnailCacheSize.
    /// </summary>
    public virtual Task<Bitmap?> LoadThumbnailAsync(Track track) => LoadAsync(track, ArtSize.Thumbnail);

    private async Task<Bitmap?> LoadAsync(Track track, ArtSize size)
    {
        if (IsLocalFile(track))
            return await LoadLocalAsync(track, size);

        return await LoadRemoteAsync(track, size);
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

    private async Task<Bitmap?> LoadLocalAsync(Track track, ArtSize size)
    {
        var key = Keyed(LocalCacheKey(track), size);

        if (TryGetCached(key, out var cached))
            return cached;

        var bmp = await Task.Run(() => LoadLocalBitmap(track, size));
        if (bmp != null)
            Retain(key, bmp);

        return bmp;
    }

    private Bitmap? LoadLocalBitmap(Track track, ArtSize size)
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
            return Decode(ms, size);
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
    // tag-then-cover-file lookup and is shared with Flower.Server, which serves
    // the same bytes at /api/flower/v1/cover-art.
    // Callers that also need to know what to serve the bytes *as* should use
    // LocalAlbumArtReader.ForFile directly rather than sniff.
    public static byte[]? TryGetLocalArtBytes(Track track) =>
        LocalAlbumArtReader.ForFile(track.Path, StaticLogger)?.Bytes;

    // The undecoded, un-downscaled art for either kind of track - what Track
    // Info's Artwork tab reports the real dimensions of and opens at full size,
    // as opposed to the MaxArtPixels bitmap LoadAsync hands back for display. A
    // placeholder track's answer is whatever LoadRemoteAsync already wrote into
    // the disk cache, so this never issues a fetch of its own: no cached copy
    // means no bytes, which is the same "nothing to show" the caller handles
    // anyway. That path also has no MIME type to report - the cache is keyed by
    // album id and stores bytes alone - hence the empty string rather than a
    // guess sniffed back out of the bytes.
    public static LocalAlbumArt? TryGetArt(Track track)
    {
        if (IsLocalFile(track))
            return LocalAlbumArtReader.ForFile(track.Path, StaticLogger);

        if (track.OriginAlbumArtId is not { Length: > 0 } artId)
            return null;

        var cachePath = Path.Combine(CacheDirectory, $"{artId}.art");
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

    // Whether these songs all carry the same cover - none at all counts, so a
    // selection where some have one and some do not does not. Track Info shows
    // "Multiple Album Art" when they do not, rather than the first song's
    // cover as if it were everyone's. Songs of the server's name their cover
    // by album (LibraryDtoMapper's CoverArt), so for those the name is enough;
    // anything else is read and compared, stopping at the first that differs.
    // Reads files, so off the UI thread.
    public static bool ShareOneCover(IReadOnlyList<Track> tracks)
    {
        if (tracks.Count < 2)
            return true;
        if (tracks.All(t => !string.IsNullOrEmpty(t.OriginAlbumArtId)))
            return tracks.All(t => t.OriginAlbumArtId == tracks[0].OriginAlbumArtId);

        var first = TryGetArt(tracks[0])?.Bytes ?? [];
        for (var i = 1; i < tracks.Count; i++)
        {
            var bytes = TryGetArt(tracks[i])?.Bytes ?? [];
            if (!bytes.AsSpan().SequenceEqual(first))
                return false;
        }
        return true;
    }

    // The art's own pixel size - what Track Info writes under the cover, on
    // both heads. Read from the header (ImageHeader), and decoded only for a
    // format that cannot read; both views used to decode the whole picture for
    // it. Null for art that will not decode at all, which shows as the
    // placeholder everywhere and here just means no size.
    public static PixelSize? MeasureArt(byte[] bytes)
    {
        if (ImageHeader.TryReadSize(bytes) is { } size)
            return new PixelSize(size.Width, size.Height);

        try
        {
            using var stream = new MemoryStream(bytes);
            using var full = new Bitmap(stream);
            return full.PixelSize;
        }
        catch
        {
            return null;
        }
    }

    // Bumped by Invalidate below. Views that have already published a
    // bitmap compare against this when their art is next read: a rewritten
    // cover leaves the Track instance untouched, so nothing in the row-merge
    // path (TrackRowViewModel.ArtSourceMatches) can notice. Deliberately not
    // per-album - an invalidation is rare (a user replacing artwork), and the
    // cost of the over-broad answer is one re-read of a file that is almost
    // certainly still in the OS cache.
    public static int CacheGeneration => Volatile.Read(ref _cacheGeneration);
    private static int _cacheGeneration;

    // What reads the art again - comparing against CacheGeneration - is a
    // view asking for it, and a view asks only when told the art changed. So a
    // screen already showing a cover kept showing the old one: on the phone,
    // the album page kept alive under Track Info, which the user goes straight
    // back to. Each holder that has published a cover is remembered here and
    // told when one is replaced, and raises its own change if what it showed
    // is now stale; whatever is showing it reads it again, and the read
    // reloads it. Weakly, and only once a cover has been shown: a holder
    // nothing draws any more is collected out of the table, and one never
    // drawn is not in it - telling a library's worth of rows to load their
    // covers because one changed would be the opposite of lazy.
    public interface IShowsAlbumArt
    {
        void AlbumArtReplaced();
    }

    private static readonly ConditionalWeakTable<IShowsAlbumArt, object?> Showing = new();
    private static int _replacementPosted;

    public static void Shown(IShowsAlbumArt holder) => Showing.AddOrUpdate(holder, null);

    // Once per batch of invalidations - an album's worth of tracks rewritten
    // together is one replacement - and on the UI thread, where the holders'
    // changes are raised.
    private static void TellShowing()
    {
        if (Interlocked.Exchange(ref _replacementPosted, 1) != 0)
            return;

        Dispatcher.UIThread.Post(() =>
        {
            Volatile.Write(ref _replacementPosted, 0);
            var holders = new List<IShowsAlbumArt>();
            foreach (var (holder, _) in Showing)
                holders.Add(holder);
            foreach (var holder in holders)
                holder.AlbumArtReplaced();
        });
    }

    // Drops a track's album whose art was just rewritten (Track Info's Artwork
    // tab) out of the caches, so the next LoadAsync fetches it again instead of
    // handing back the bitmap decoded from the old bytes. Keyed the same way
    // LoadLocalAsync/LoadRemoteAsync key it, hence the shared LocalCacheKey
    // rather than a second copy of that rule.
    //
    // The remote half is not symmetric with the local one, and has to do more.
    // A local file is re-read on the next miss and is current by definition,
    // but synced art is cached on disk under the origin's album id, which does
    // not change when the art behind it does. So the cache file has to be
    // deleted outright; leaving it would mean the next load finds the old
    // picture under the same key and never asks the server.
    //
    // The evicted Bitmap is deliberately not disposed: rows and tiles currently
    // on screen may still be painting it, exactly as in Retain's own eviction.
    public static void Invalidate(Track track)
    {
        Interlocked.Increment(ref _cacheGeneration);
        TellShowing();

        Forget(LocalCacheKey(track));
        Forget(Keyed(LocalCacheKey(track), ArtSize.Thumbnail));

        if (track.OriginAlbumArtId is { Length: > 0 } artId)
        {
            Forget($"remote:{artId}");
            Forget(Keyed($"remote:{artId}", ArtSize.Thumbnail));
            var cachePath = Path.Combine(CacheDirectory, $"{artId}.art");
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
        StrongCache.Forget(key);
        StrongThumbnailCache.Forget(key);
    }

    // Fetches a placeholder track's album art from its origin peer and keeps it
    // on disk under the origin's album id, so a restart (or the peer going
    // offline) does not mean re-fetching. The key names the album and not the
    // bytes - see Track.OriginAlbumArtId - so art replaced on the origin is not
    // noticed here on its own; Invalidate above is what drops a stale entry.
    private async Task<Bitmap?> LoadRemoteAsync(Track track, ArtSize size)
    {
        var artId = track.OriginAlbumArtId;
        if (string.IsNullOrEmpty(artId) || string.IsNullOrEmpty(track.OriginDeviceFingerprint))
            return null;

        var cacheKey = Keyed($"remote:{artId}", size);
        if (TryGetCached(cacheKey, out var cached))
            return cached;

        var cachePath = Path.Combine(CacheDirectory, $"{artId}.art");
        if (File.Exists(cachePath))
        {
            var bmp = await Task.Run(() => TryDecodeFile(cachePath, size));
            if (bmp != null)
            {
                Retain(cacheKey, bmp);
                return bmp;
            }

            // Nothing on disk under this id is ever going to become decodable
            // - the file is truncated, or it predates the decode-first rule
            // below and is actually a Subsonic error envelope. Drop it so the
            // fetch beneath re-fills it, instead of warning about the same
            // unreadable file on every load for the life of the cache.
            DiscardCacheFile(cachePath);
        }

        if (KnownBare.ContainsKey(artId))
            return null;

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
            // Null is a fetch that failed, and empty is the origin saying it
            // has no picture - only the second is worth remembering.
            var bytes = await FetchArtBytesAsync(track, url);
            if (bytes == null)
                return null;
            if (bytes.Length == 0)
            {
                KnownBare[artId] = 0;
                return null;
            }

            // Decode off the UI thread - same reason LoadLocalAsync/the cached-file
            // path above both use Task.Run: this runs on whatever thread called
            // LoadAsync (typically the UI thread, via TrackRowViewModel.AlbumArt's
            // getter), and decoding a full-size image inline there stalls scrolling
            // every time a placeholder row's art finishes downloading.
            var bmp = await Task.Run(() => TryDecodeBytes(bytes, size));
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

            await WriteCacheFileAsync(cachePath, bytes);

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

    // Set by a 429: no batch goes out before then (see WaitOutAsync).
    private DateTimeOffset _throttledUntil;

    // Retry-After as the server sends it is a minute (RateLimitResponse);
    // this only bounds a header that says something stranger.
    private static readonly TimeSpan MaxThrottle = TimeSpan.FromMinutes(2);

    // One album's art, and everything needed to ask for it either way: the
    // batch id, the single-request URL that is the fallback, and the callers
    // waiting on the answer. Several tracks from the same album collapse onto
    // one of these, which is most of what the batching buys on a track list.
    private sealed record PendingArt(string Endpoint, string Id, string Url, TaskCompletionSource<byte[]?> Completion);

    // The bytes for one track's art, asked for in company where that is
    // possible. Empty for "no art", which is a real answer and not an error -
    // plenty of albums have no picture - and null for a fetch that failed.
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

            TimeSpan throttled;
            lock (_batchLock)
            {
                throttled = _throttledUntil - DateTimeOffset.UtcNow;
            }
            if (throttled > TimeSpan.Zero)
                await Task.Delay(throttled);

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
            // Source-generated metadata rather than reflection: on Flower.Web this
            // is the difference between a batch request and a NotSupportedException,
            // and the shape it writes is the context's own PascalCase, which is
            // what SyncEndpoints has always been reading.
            var payload = JsonSerializer.SerializeToUtf8Bytes(
                new CoverArtBatchRequest { Ids = batch.Select(entry => entry.Id).ToList() },
                FlowerJsonContext.Default.CoverArtBatchRequest);

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
            if (response.StatusCode == HttpStatusCode.TooManyRequests)
            {
                WaitOut(batch, response.Headers.RetryAfter);
                return;
            }
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
                    entry.Completion.TrySetResult(bytes);
                else
                    entry.Completion.TrySetResult(await FetchOneAsync(entry.Url));
            }
        }
        catch (Exception ex)
        {
            await FallBackToSingleAsync(batch, ex.Message);
        }
    }

    // Throttled: the batch goes back in the queue, and nothing more is sent
    // until the server's Retry-After has passed. Being throttled is not the
    // art failing - and asking for each album on its own instead, which this
    // used to do, is up to 32 more requests against the budget that was just
    // spent, every one refused, which kept it spent for as long as anything
    // was on screen. The tiles wait for their covers rather than go blank.
    private void WaitOut(List<PendingArt> batch, RetryConditionHeaderValue? retryAfter)
    {
        var wait = retryAfter?.Delta
                   ?? (retryAfter?.Date is { } date ? date - DateTimeOffset.UtcNow : TimeSpan.FromSeconds(RetryAfterFallbackSeconds));
        wait = wait < TimeSpan.Zero ? TimeSpan.Zero : wait > MaxThrottle ? MaxThrottle : wait;

        lock (_batchLock)
        {
            var until = DateTimeOffset.UtcNow + wait;
            if (until > _throttledUntil)
                _throttledUntil = until;

            foreach (var entry in batch)
            {
                // Asked for again while this batch was out: the later caller's
                // answer is this one's too.
                if (_waiting.TryGetValue(entry.Id, out var later))
                    later.Completion.Task.ContinueWith(t => entry.Completion.TrySetResult(t.Result), TaskScheduler.Default);
                else
                    _waiting[entry.Id] = entry;
            }
        }

        _logger.LogDebug("Album art for {Count} albums was refused for now (429); asking again in {Seconds:0}s", batch.Count, wait.TotalSeconds);
    }

    private const int RetryAfterFallbackSeconds = 60;

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
            // Signed, like every other call this app makes into a peer. This
            // used to send a bare fingerprint and alias with no signature at
            // all, which the app's own listener tolerated but Flower.Server
            // does not - and back when art was fetched over /rest, its refusal
            // was a *Subsonic* refusal, so it came back as HTTP 200 carrying an
            // error envelope. Two things followed from that, both of them bad:
            // the JSON error body was written straight into the art cache as if
            // it were an image (see LoadRemoteAsync's decode-before-cache rule),
            // and each refusal charged the server's FailedAuthLimiter, so ten
            // album tiles were enough to 429 the whole of /rest for a minute -
            // including /rest/stream, which is why playback of server-hosted
            // tracks died wholesale. Art and playback have both since moved to
            // Flower's own surface, where they are separately budgeted (see
            // SyncEndpoints' three limiters) and a refusal is a status code.
            await request.AddPeerCredentialsAsync(_credentials!);
            if (_artUrls!.ClosesConnection)
                request.Headers.ConnectionClose = true;

            using var response = await Http.SendAsync(request);
            if (response.StatusCode == HttpStatusCode.NotFound)
                return [];
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
    // would drag the whole sync JSON context across for them. Internal rather
    // than private only so FlowerJsonContext can name it - this is one of the
    // shapes the browser head puts on the wire, and a trimmed head has no
    // reflection to fall back on.
    internal sealed class CoverArtBatchRequest
    {
        public List<string> Ids { get; set; } = [];
    }

    // A row's thumbnail and a tile's full-size cover can fetch the same album
    // at the same moment, and both then write it here. The picture is already
    // decoded by then, so a write that loses that race costs only the disk copy
    // - not the art on screen, which a failure inside LoadRemoteAsync's own try
    // would have turned into the placeholder.
    private async Task WriteCacheFileAsync(string cachePath, byte[] bytes)
    {
        try
        {
            Directory.CreateDirectory(CacheDirectory);
            await File.WriteAllBytesAsync(cachePath, bytes);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Could not write fetched album art to the cache at {Path}", cachePath);
        }
    }

    private Bitmap? TryDecodeBytes(byte[] bytes, ArtSize size)
    {
        try
        {
            using var ms = new MemoryStream(bytes);
            return Decode(ms, size);
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

    private Bitmap? TryDecodeFile(string path, ArtSize size)
    {
        try
        {
            using var stream = File.OpenRead(path);
            return Decode(stream, size);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Could not decode cached remote album art at {Path}; discarding it and re-fetching", path);
            return null;
        }
    }
}
