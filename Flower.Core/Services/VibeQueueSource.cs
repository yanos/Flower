using System;
using System.Collections.Generic;
using System.Linq;

using Flower.Models;

namespace Flower.Services;

// What keeps a Vibe Playlist's endless queue going (docs/VIBE-PLAYLIST-PLAN.md,
// "The endless queue"): it draws the next song from the playlist's pool,
// which is in score order, as a weighted shuffle under three rules.
//
//  - No repeats until most of the pool has played. Songs queued are
//    remembered for the session, and none comes back while another is left;
//    once fewer than a quarter are left the source asks for more songs
//    (Extend), and if none come it forgets all but the latest quarter
//    rather than stopping.
//  - Never two in a row from one album, and an artist at most once in any
//    four songs, unless the pool is too small to allow it.
//  - A song skipped in its first 30 seconds keeps its place, at a quarter of
//    its weight, for this playlist.
//
// Where a device keeps the songs skipped in each Vibe Playlist
// (VibeSkipRepository); a head with no database keeps them for the session.
public interface IVibeSkipStore
{
    IReadOnlySet<Guid> Load(Guid playlistId);

    void Record(Guid playlistId, Guid trackId);
}

// Pure: no I/O and no threads. Its owner (PlaylistControlViewModel) asks for
// songs as the queue runs low, and hands it whatever an extension brought.
public sealed class VibeQueueSource
{
    // How many songs the queue keeps ahead of the one playing.
    public const int Ahead = 10;

    // The rest of the pool, as a share of it, below which the source asks for
    // more songs.
    public const double ExtendBelow = 0.25;

    // Skipped this soon into a song, it counts against the song.
    public static readonly TimeSpan SkipWithin = TimeSpan.FromSeconds(30);

    public const double SkippedWeight = 0.25;

    // An artist at most once in this many songs in a row.
    private const int ArtistSpacing = 4;

    private readonly List<Track> _pool;
    // Each song's place in the ranking, the pool's order.
    private readonly Dictionary<Guid, int> _rank = [];
    private readonly HashSet<Guid> _skipped;
    private readonly Random _random;

    // Every song queued this session, oldest first, and the ones not yet
    // forgiven - see Forget.
    private readonly List<Track> _queued = [];
    private readonly HashSet<Guid> _used = [];

    public VibeQueueSource(Playlist playlist, IEnumerable<Guid>? skipped = null, Random? random = null)
    {
        Playlist = playlist;
        _pool = playlist.Tracks.DistinctBy(t => t.Id).ToList();
        for (var i = 0; i < _pool.Count; i++)
            _rank[_pool[i].Id] = i;
        _skipped = skipped?.ToHashSet() ?? [];
        _random = random ?? Random.Shared;
    }

    public Playlist Playlist { get; }

    public IReadOnlyList<Track> Pool => _pool;

    // Songs added beyond the playlist's own pool, by the server's id - what an
    // extension request names so it is not sent the same songs again.
    public IEnumerable<Track> Extensions => _pool.Skip(Playlist.Tracks.Count);

    // True once fewer than a quarter of the pool are left to play: the owner
    // asks for more (a paired device's one request), and calls Add with what
    // came back - or nothing, which is fine too.
    public bool WantsMore => _pool.Count > 0 && _pool.Count(t => !_used.Contains(t.Id)) < _pool.Count * ExtendBelow;

    // Songs from an extension, at the bottom of the ranking: they join the pool
    // for the rest of the session.
    public void Add(IEnumerable<Track> tracks)
    {
        foreach (var track in tracks)
        {
            if (_rank.TryAdd(track.Id, _pool.Count))
                _pool.Add(track);
        }
    }

    public void RecordSkip(Track track) => _skipped.Add(track.Id);

    // A song queued by other means - the one picked to start the playlist
    // from - counts as queued, so it isn't drawn again straight away.
    public void Queued(Track track)
    {
        if (_rank.ContainsKey(track.Id) && _used.Add(track.Id))
            _queued.Add(track);
    }

    public bool WasSkipped(Track track) => _skipped.Contains(track.Id);

    // The next song, or null for an empty pool. `recent` is what was queued
    // just before, newest last, for the album and artist rules: the queue
    // itself, since the owner may have lined songs up by hand between.
    public Track? Next(IReadOnlyList<Track> recent)
    {
        if (_pool.Count == 0)
            return null;

        var fresh = _pool.Where(t => !_used.Contains(t.Id)).ToList();
        if (fresh.Count == 0)
        {
            Forget();
            fresh = _pool.Where(t => !_used.Contains(t.Id)).ToList();
        }

        var spaced = fresh.Where(t => Spaced(t, recent)).ToList();
        var pick = Weighted(spaced.Count > 0 ? spaced : fresh);
        _queued.Add(pick);
        _used.Add(pick.Id);
        return pick;
    }

    // All but the most recent quarter of the pool may come back.
    private void Forget()
    {
        var keep = _pool.Count / 4;
        _used.Clear();
        foreach (var track in _queued.TakeLast(keep))
            _used.Add(track.Id);
    }

    private static bool Spaced(Track candidate, IReadOnlyList<Track> recent)
    {
        if (recent.Count == 0)
            return true;

        var last = recent[^1];
        // One album: the same title, and the same album artist where the tags
        // name one - a compilation's songs have an artist each.
        if (!string.IsNullOrEmpty(candidate.Album)
            && string.Equals(candidate.Album, last.Album, StringComparison.OrdinalIgnoreCase)
            && string.Equals(candidate.AlbumArtists ?? "", last.AlbumArtists ?? "", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (string.IsNullOrEmpty(candidate.Artists))
            return true;

        for (var i = Math.Max(0, recent.Count - (ArtistSpacing - 1)); i < recent.Count; i++)
        {
            if (string.Equals(recent[i].Artists, candidate.Artists, StringComparison.OrdinalIgnoreCase))
                return false;
        }

        return true;
    }

    // Weighted by place in the ranking: the best song four times as likely as
    // the last, and a skipped one at a quarter of its own.
    private Track Weighted(List<Track> candidates)
    {
        var weights = new double[candidates.Count];
        var total = 0.0;
        for (var i = 0; i < candidates.Count; i++)
        {
            var rank = _rank[candidates[i].Id];
            var weight = 1.0 - 0.75 * rank / Math.Max(1, _pool.Count);
            if (_skipped.Contains(candidates[i].Id))
                weight *= SkippedWeight;
            weights[i] = weight;
            total += weight;
        }

        var roll = _random.NextDouble() * total;
        for (var i = 0; i < candidates.Count; i++)
        {
            roll -= weights[i];
            if (roll <= 0)
                return candidates[i];
        }

        return candidates[^1];
    }
}
