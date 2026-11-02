using System;
using System.Collections.Generic;
using System.Threading;

using Flower.Models;

using Microsoft.Extensions.Logging;

namespace Flower.SemanticAudio;

public enum SemanticAudioState
{
    // The master switch is off: no model, no analysis.
    Off,
    Downloading,
    // Building the index from what is already analysed.
    Preparing,
    Analysing,
    UpToDate,
    // Waiting for a library scan to finish (Pause): a scan rewrites the
    // catalog the analyzer's plan is made from, and both want the disk.
    Paused,
    // Stopped on an error; Retry starts again.
    Failed,
}

// What Vibe Playlists keep on this host, for the settings page's "Using n MB".
public sealed record SemanticAudioStorage(long ModelBytes, long AnalysisBytes);

public sealed record SemanticAudioStatus(
    SemanticAudioState State,
    ModelDownloadProgress? Download,
    SemanticAudioProgress Analysis,
    int SongsSearchable,
    string? Error);

// This host can't do the work at all - no decoder for its platform - which no
// retry changes and no stack trace explains.
public sealed class SemanticAudioUnavailableException(string message) : InvalidOperationException(message);

// Asked for a ranking before there is anything to rank with.
public sealed class SemanticAudioNotReadyException(SemanticAudioState state)
    : InvalidOperationException($"Vibe Playlists can't search yet ({state}).")
{
    public SemanticAudioState State { get; } = state;
}

// The whole of Vibe Playlists' engine on one host (docs/VIBE-PLAYLIST-PLAN.md,
// "Where it runs"): the model download, the analysis job, the index and the
// search, behind the two settings - the master switch and clips per song.
// The same code on a server, for everything paired to it, and in-process on
// a device on its own; nothing here knows which. The encoders come from the
// host (Flower.SemanticAudio.Onnx's, given a model file), which is what keeps
// ONNX Runtime out of Flower.Core.
//
// All the work happens on one thread of its own, below normal priority, so
// that playback on the same machine never stutters for it and a NAS still
// serves its streams. Turning the switch off is done on that thread too, so a
// purge never races a song being written.
public sealed class SemanticAudioEngine : IDisposable
{
    private readonly Library _library;
    private readonly SemanticAudioRepository _repository;
    private readonly SemanticAudioModelStore _store;
    private readonly IClipSource _clips;
    private readonly ILogger _logger;
    private readonly Func<string, IClapAudioEncoder> _openAudio;
    private readonly Func<string, RobertaTokenizer, IClapTextEncoder> _openText;
    private readonly Thread _thread;
    private readonly AutoResetEvent _wake = new(false);
    private readonly object _gate = new();

    private bool _enabled;
    private int _clipsPerSong = ClipGrid.DefaultClipsPerSong;
    private bool _paused;
    private bool _purge;
    private bool _disposed;
    private CancellationTokenSource _pass = new();

    // Owned by the work thread; read by searches.
    private IClapTextEncoder? _text;
    private FilterDirections? _directions;
    private volatile SemanticAudioSearch? _search;
    private SemanticAudioAnalyzer? _analyzer;

    private SemanticAudioState _state = SemanticAudioState.Off;
    private ModelDownloadProgress? _download;
    private string? _error;

    public SemanticAudioEngine(
        Library library,
        SemanticAudioRepository repository,
        SemanticAudioModelStore store,
        IClipSource clips,
        ILogger logger,
        Func<string, IClapAudioEncoder> openAudio,
        Func<string, RobertaTokenizer, IClapTextEncoder> openText)
    {
        _library = library;
        _repository = repository;
        _store = store;
        _clips = clips;
        _logger = logger;
        _openAudio = openAudio;
        _openText = openText;

        _library.LibraryChanged += OnLibraryChanged;
        _library.TrackChanged += OnTrackChanged;

        _thread = new Thread(Work)
        {
            IsBackground = true,
            Priority = ThreadPriority.BelowNormal,
            Name = "Semantic audio",
        };
        _thread.Start();
    }

    public bool Enabled
    {
        get
        {
            lock (_gate)
                return _enabled;
        }
    }

    public int ClipsPerSong
    {
        get
        {
            lock (_gate)
                return _clipsPerSong;
        }
    }

    public SemanticAudioStatus Status
    {
        get
        {
            lock (_gate)
            {
                return new SemanticAudioStatus(
                    _state,
                    _download,
                    _analyzer?.Progress ?? SemanticAudioProgress.None,
                    _search?.Index.Count ?? 0,
                    _error);
            }
        }
    }

    // The two settings. Turning the switch off stops the job and deletes the
    // model and every embedding; changing the clip setting rebuilds the index
    // from the clips already stored (seconds) and analyses only what is
    // missing, while searches go on against the old index meanwhile.
    public void Configure(bool enabled, int clipsPerSong)
    {
        if (!ClipGrid.IsValidSetting(clipsPerSong))
            throw new ArgumentOutOfRangeException(nameof(clipsPerSong), clipsPerSong, "Clips per song is a positive count or WholeSong.");

        lock (_gate)
        {
            if (enabled == _enabled && clipsPerSong == _clipsPerSong)
                return;

            if (_enabled && !enabled)
                _purge = true;

            _enabled = enabled;
            _clipsPerSong = clipsPerSong;
            _error = null;
            RestartPass();
        }
    }

    // While the host's library is being scanned: the pass in progress stops
    // (the song it was on is heard again later), and no pass starts until
    // Resume. The model still downloads and searches go on meanwhile.
    public void Pause()
    {
        lock (_gate)
        {
            if (_paused)
                return;

            _paused = true;
            RestartPass();
        }
    }

    public void Resume()
    {
        lock (_gate)
        {
            if (!_paused)
                return;

            _paused = false;
            RestartPass();
        }
    }

    // The model's files (a download in progress included) and the clips
    // stored, for the settings page. Reads the disk and the database, so it
    // is for a page asking, not for every status poll's hot path.
    public SemanticAudioStorage Storage() =>
        new(_store.BytesOnDisk, _repository.ClipCount() * Embedding.StoredBytes);

    // The songs that couldn't be analysed, and why.
    public IReadOnlyList<(Guid TrackId, string Reason)> Failures() => _repository.LoadFailures();

    // After an error: start again from wherever it stopped.
    public void Retry()
    {
        lock (_gate)
        {
            _error = null;
            RestartPass();
        }
    }

    public IReadOnlyList<SemanticAudioMatch> Rank(SemanticAudioQuery query, int limit = SemanticAudioSearch.Candidates)
    {
        var search = _search;
        if (!Enabled || search is null || search.Index.Count == 0)
            throw new SemanticAudioNotReadyException(Status.State);

        var byId = _library.Snapshot.ById;
        return search.Rank(query, id => byId.GetValueOrDefault(id), limit);
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
                return;

            _disposed = true;
            _pass.Cancel();
        }

        _library.LibraryChanged -= OnLibraryChanged;
        _library.TrackChanged -= OnTrackChanged;
        _wake.Set();
        _thread.Join();
        _text?.Dispose();
        _wake.Dispose();
    }

    private void OnLibraryChanged(object? sender, EventArgs e) => _wake.Set();

    private void OnTrackChanged(object? sender, TrackChangedEventArgs e)
    {
        if ((e.Change & TrackChange.File) != 0)
            _wake.Set();
    }

    // Under _gate.
    private void RestartPass()
    {
        _pass.Cancel();
        _pass = new CancellationTokenSource();
        _wake.Set();
    }

    private void Work()
    {
        while (true)
        {
            _wake.WaitOne();

            bool enabled, purge, paused;
            int clipsPerSong;
            CancellationToken cancellation;
            lock (_gate)
            {
                if (_disposed)
                    return;

                enabled = _enabled;
                purge = _purge;
                _purge = false;
                paused = _paused;
                clipsPerSong = _clipsPerSong;
                cancellation = _pass.Token;

                // Stopped on an error: only Retry, a setting changing or the
                // switch going off moves it on.
                if (_error != null && !purge)
                    continue;
            }

            if (purge)
                Purge();

            if (!enabled)
                continue;

            try
            {
                Run(clipsPerSong, paused, cancellation);
            }
            catch (OperationCanceledException)
            {
                // A setting changed, or the switch went off: the next turn
                // of the loop picks up whatever it is now.
            }
            catch (Exception ex)
            {
                if (ex is SemanticAudioUnavailableException or ModelDownloadException)
                    _logger.LogWarning("Semantic audio stopped: {Reason}", ex.Message);
                else
                    _logger.LogError(ex, "Semantic audio stopped");
                lock (_gate)
                {
                    _state = SemanticAudioState.Failed;
                    _error = ex is ModelDownloadException ? "Couldn't download the model: " + ex.Message : ex.Message;
                }
            }
        }
    }

    private void Run(int clipsPerSong, bool paused, CancellationToken cancellation)
    {
        if (!_store.IsPresent)
        {
            SetState(SemanticAudioState.Downloading);
            var progress = new SynchronousProgress<ModelDownloadProgress>(p =>
            {
                lock (_gate)
                    _download = p;
            });
            _store.DownloadAsync(progress, cancellation).GetAwaiter().GetResult();
            lock (_gate)
                _download = null;
        }

        cancellation.ThrowIfCancellationRequested();
        var manifest = _store.Manifest;
        if (_text is null)
        {
            var tokenizer = RobertaTokenizer.Load(_store.PathOf(manifest.Vocabulary), _store.PathOf(manifest.Merges));
            _text = _openText(_store.PathOf(manifest.TextEncoder), tokenizer);
            _directions = FilterDirections.From(_text);
        }

        if (_search is null || _search.Index.ClipsPerSong != clipsPerSong)
        {
            SetState(SemanticAudioState.Preparing);
            var index = SemanticAudioIndex.Build(_repository, _directions!, clipsPerSong, manifest.AnalysisModel);
            _search = new SemanticAudioSearch(index, _repository, _text);
        }

        // Paused only stops the listening: the download and the index don't
        // touch the catalog, and searches shouldn't wait out a long scan.
        cancellation.ThrowIfCancellationRequested();
        if (paused)
        {
            SetState(SemanticAudioState.Paused);
            return;
        }

        if (!_clips.IsAvailable)
            throw new SemanticAudioUnavailableException("This host has no audio decoder for its platform, so it can't listen to songs.");

        if (_analyzer is null)
        {
            _analyzer = new SemanticAudioAnalyzer(_repository, _clips, () => _openAudio(_store.PathOf(manifest.AudioEncoder)),
                manifest.AnalysisModel, _logger);
            _analyzer.SongAnalysed += (song, clips) => _search?.Index.Put(song, clips);
            _analyzer.SongsRemoved += ids => _search?.Index.Remove(ids);
        }

        SetState(SemanticAudioState.Analysing);
        _analyzer.RunPass(_library.Snapshot.Tracks, clipsPerSong, cancellation);
        SetState(SemanticAudioState.UpToDate);
    }

    private void Purge()
    {
        _logger.LogInformation("Semantic audio switched off: deleting the model and every analysis");
        lock (_gate)
        {
            _search = null;
            _analyzer = null;
            _directions = null;
            _download = null;
            _error = null;
            _state = SemanticAudioState.Off;
        }

        _text?.Dispose();
        _text = null;
        _repository.DeleteAll();
        _store.Delete();
    }

    private void SetState(SemanticAudioState state)
    {
        lock (_gate)
            _state = state;
    }

    // Progress<T> posts to a captured context; this reports on the thread
    // that called, which here is the engine's own.
    private sealed class SynchronousProgress<T>(Action<T> report) : IProgress<T>
    {
        public void Report(T value) => report(value);
    }
}
