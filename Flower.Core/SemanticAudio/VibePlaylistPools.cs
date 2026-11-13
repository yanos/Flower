using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using Microsoft.Extensions.Logging;

using Flower.Logging;
using Flower.Models;

namespace Flower.SemanticAudio;

// Every Vibe Playlist's pool on the host that ranks songs
// (docs/VIBE-PLAYLIST-PLAN.md, "Search"): the head of its sentence's ranking,
// installed as the playlist's Tracks with Playlist.Materialize, so that sync,
// Add to Queue, Download All and the track list need nothing of their own.
// On a server for everyone paired to it; in-process on a device on its own.
//
// A pool is rebuilt when its sentence or No vocals switch changes (a device
// pushing an edit, the editor saving one), when the library changes, and as
// the analysis goes on: at most once an hour while it is under way, and once
// when it is done, since a pool is only ever made of the songs heard so far.
// Like a smart playlist's recomputation, a rebuild does not move UpdatedAt:
// it is not an edit. A paired device learns of it from /info's playlists
// token, which counts a Vibe Playlist's songs (PlaylistSyncMapper.TokenOf).
public sealed class VibePlaylistPools : IDisposable
{
    // The most songs a pool holds. Past it, the endless queue asks for more
    // (Extend) rather than every device carrying the whole ranking.
    public const int MaxPoolSize = 200;

    // How far below the best song's score the pool stops: a library where
    // little fits makes a short pool rather than 200 poor matches. Not settled
    // by phase 0, which ranked 1,000 songs: there, the 25th song of 28 prompts
    // sat 0.07 to 0.24 below the first. 0.15 is a start, to be set by
    // listening to previews of real prompts on the whole library.
    public const float PoolMargin = 0.15f;

    // What a preview shows (POST /vibe/preview).
    public const int PreviewSize = 50;

    // How many songs one extension adds.
    public const int ExtendSize = 50;

    // Not const, so a test can shorten them.
    internal static TimeSpan Cooldown = TimeSpan.FromMilliseconds(500);
    internal static TimeSpan AnalysisRebuildInterval = TimeSpan.FromHours(1);
    internal static TimeSpan StatusPollInterval = TimeSpan.FromMinutes(1);

    private readonly Library _library;
    private readonly Func<SemanticAudioQuery, int, IReadOnlyList<SemanticAudioMatch>> _rank;
    private readonly Func<SemanticAudioStatus> _status;
    private readonly Func<int> _clipsPerSong;
    private readonly ILogger _logger;
    private readonly TimeProvider _clock;
    private readonly object _gate = new();

    // What each playlist's pool was last ranked from, and against which
    // generation of the index: a pool whose sentence, switch and generation
    // all match is left alone.
    private readonly Dictionary<Guid, (string Prompt, bool Instrumental, int Generation)> _built = new();
    private int _generation;
    private (SemanticAudioState State, int Searchable, int ClipsPerSong) _seen;
    private DateTimeOffset _lastAnalysisRebuild = DateTimeOffset.MinValue;

    private CancellationTokenSource? _pending;
    private ITimer? _poll;
    private bool _started;
    private bool _disposed;

    public VibePlaylistPools(SemanticAudioEngine engine, Library library, ILogger logger, TimeProvider? clock = null)
        : this(library, (query, limit) => engine.Rank(query, limit), () => engine.Status, () => engine.ClipsPerSong, logger, clock)
    {
    }

    // The seam the tests use: a ranking and a status of their own, with no
    // model behind them.
    internal VibePlaylistPools(
        Library library,
        Func<SemanticAudioQuery, int, IReadOnlyList<SemanticAudioMatch>> rank,
        Func<SemanticAudioStatus> status,
        Func<int> clipsPerSong,
        ILogger logger,
        TimeProvider? clock = null)
    {
        _library = library;
        _rank = rank;
        _status = status;
        _clipsPerSong = clipsPerSong;
        _logger = logger;
        _clock = clock ?? TimeProvider.System;
    }

    public void Start()
    {
        if (_started)
            return;

        _started = true;
        _library.PlaylistsChanged += OnPlaylistsChanged;
        _library.LibraryChanged += OnLibraryChanged;
        _seen = Seen();
        _poll = _clock.CreateTimer(_ => PollStatus(), null, StatusPollInterval, StatusPollInterval);
        Schedule();
    }

    private void OnPlaylistsChanged(object? sender, EventArgs e) => Schedule();

    private void OnLibraryChanged(object? sender, EventArgs e)
    {
        lock (_gate)
            _generation++;
        Schedule();
    }

    // The analysis moving on, read from the engine's status rather than an
    // event of its own: a rebuild is cheap, but not something to do per song.
    internal void PollStatus()
    {
        var now = Seen();
        bool rebuild;
        lock (_gate)
        {
            if (now == _seen)
                return;

            var previous = _seen;
            _seen = now;

            // The index gaining songs while the analysis runs: once an hour.
            // Anything else - the analysis finishing, the index rebuilt for a
            // new clip setting, the switch going off and on - at once.
            var onlyGrew = previous.State == SemanticAudioState.Analysing && now.State == SemanticAudioState.Analysing
                && previous.ClipsPerSong == now.ClipsPerSong;
            rebuild = !onlyGrew || _clock.GetUtcNow() - _lastAnalysisRebuild >= AnalysisRebuildInterval;
            if (rebuild)
            {
                _generation++;
                _lastAnalysisRebuild = _clock.GetUtcNow();
            }
        }

        if (rebuild)
            Schedule();
    }

    private (SemanticAudioState State, int Searchable, int ClipsPerSong) Seen()
    {
        var status = _status();
        return (status.State, status.SongsSearchable, _clipsPerSong());
    }

    public void Schedule()
    {
        if (_disposed)
            return;

        CancellationTokenSource started;
        lock (_gate)
        {
            _pending?.Cancel();
            _pending?.Dispose();
            _pending = started = new CancellationTokenSource();
        }

        _ = RunAfterCooldownAsync(started.Token);
    }

    private async Task RunAfterCooldownAsync(CancellationToken token)
    {
        try
        {
            await Task.Delay(Cooldown, _clock, token);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        try
        {
            Refresh();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Vibe Playlists' pools were not rebuilt");
            CrashReporting.ReportCaught(ex, "vibe pools");
        }
    }

    // Rebuilds every pool that is out of date, now. Returns the playlists
    // whose songs changed.
    public IReadOnlyList<Playlist> Refresh()
    {
        lock (_gate)
        {
            var changed = new List<Playlist>();
            var vibes = _library.Playlists.Where(p => p.IsVibe).ToList();
            foreach (var playlist in vibes)
            {
                var key = (playlist.VibePrompt!, playlist.VibeInstrumental, _generation);
                if (_built.TryGetValue(playlist.Id, out var built) && built == key)
                    continue;

                IReadOnlyList<SemanticAudioMatch> ranked;
                try
                {
                    ranked = _rank(new SemanticAudioQuery(playlist.VibePrompt!, playlist.VibeInstrumental), SemanticAudioSearch.Candidates);
                }
                catch (SemanticAudioNotReadyException)
                {
                    // Nothing to rank with yet, or the switch is off: the
                    // pool keeps what it had, and the status poll brings this
                    // back once the index is there.
                    continue;
                }

                _built[playlist.Id] = key;
                if (playlist.Materialize(Resolve(Cut(ranked))))
                    changed.Add(playlist);
            }

            foreach (var gone in _built.Keys.Except(vibes.Select(p => p.Id)).ToList())
                _built.Remove(gone);

            if (changed.Count == 0)
                return [];

            // Not PlaylistsChanged, which would announce a rebuild as an edit
            // and come straight back here - see Library.SavePlaylists.
            _library.SavePlaylists();

            // The sentence stays out of the log above Debug: what someone
            // wants to listen to is their own business, even on a shared server.
            _logger.LogInformation("Rebuilt {Count} Vibe Playlist pool(s): {Sizes} song(s)",
                changed.Count, string.Join(", ", changed.Select(p => p.Tracks.Count)));
            return changed;
        }
    }

    // What the editor shows while typing pauses: the first songs of the pool
    // this sentence would make. Saves nothing.
    public IReadOnlyList<Track> Preview(string prompt, bool instrumental, int count = PreviewSize) =>
        Resolve(Cut(_rank(new SemanticAudioQuery(prompt, instrumental), SemanticAudioSearch.Candidates)).Take(count).ToList());

    // More songs for the endless queue, once most of the pool has played:
    // the next ones down the same ranking that it doesn't have yet. Not
    // stored - the next rebuild cuts the pool where it always does - so the
    // device asking keeps them for its session.
    public IReadOnlyList<Track> Extend(Playlist playlist, IReadOnlySet<Guid> exclude, int count = ExtendSize)
    {
        if (playlist.VibePrompt is not { } prompt)
            return [];

        var have = playlist.Tracks.Select(t => t.Id).ToHashSet();
        have.UnionWith(exclude);
        var ranked = _rank(new SemanticAudioQuery(prompt, playlist.VibeInstrumental), SemanticAudioSearch.Candidates);
        return Resolve(ranked.Select(m => m.TrackId).Where(id => !have.Contains(id)).Take(count).ToList());
    }

    // The head of a ranking: at most MaxPoolSize songs, and none scoring more
    // than PoolMargin below the best.
    public static IReadOnlyList<Guid> Cut(IReadOnlyList<SemanticAudioMatch> ranked, int max = MaxPoolSize, float margin = PoolMargin)
    {
        if (ranked.Count == 0)
            return [];

        var floor = ranked[0].Score - margin;
        var pool = new List<Guid>(Math.Min(max, ranked.Count));
        foreach (var match in ranked)
        {
            if (pool.Count == max || match.Score < floor)
                break;
            pool.Add(match.TrackId);
        }

        return pool;
    }

    private List<Track> Resolve(IReadOnlyList<Guid> ids)
    {
        var byId = _library.Snapshot.ById;
        return ids.Select(id => byId.GetValueOrDefault(id)).OfType<Track>().ToList();
    }

    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;
        if (_started)
        {
            _library.PlaylistsChanged -= OnPlaylistsChanged;
            _library.LibraryChanged -= OnLibraryChanged;
        }

        _poll?.Dispose();
        lock (_gate)
        {
            _pending?.Cancel();
            _pending?.Dispose();
            _pending = null;
        }
    }
}
