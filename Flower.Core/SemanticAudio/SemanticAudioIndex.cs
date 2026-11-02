using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;

namespace Flower.SemanticAudio;

// Every analysed song's mean embedding, in memory as one contiguous array
// (docs/VIBE-PLAYLIST-PLAN.md, "Storage"): 16k songs x 512 floats is 32 MB,
// and a search is one dot product per row. Built for one clip setting, from
// the clips that setting selects; the clips themselves stay in SQLite and are
// read only to rerank a search's candidates.
//
// Alongside each mean, the two filter scores, which are arithmetic on the same
// clips and so cost no analysis: the vocal score (the most vocal selected
// clip, since one sung verse is enough for a song to have lyrics) and the
// speech score (on the mean).
public sealed class SemanticAudioIndex
{
    private readonly ReaderWriterLockSlim _lock = new();
    private readonly FilterDirections _directions;

    private Guid[] _ids = [];
    private float[] _means = [];
    private float[] _vocal = [];
    private float[] _speech = [];
    private int[][] _selected = [];
    private int _count;
    private readonly Dictionary<Guid, int> _rows = new();

    public SemanticAudioIndex(FilterDirections directions, int clipsPerSong)
    {
        _directions = directions;
        ClipsPerSong = clipsPerSong;
    }

    public int ClipsPerSong { get; }

    public int Count
    {
        get
        {
            _lock.EnterReadLock();
            try
            {
                return _count;
            }
            finally
            {
                _lock.ExitReadLock();
            }
        }
    }

    public static SemanticAudioIndex Build(SemanticAudioRepository repository, FilterDirections directions, int clipsPerSong, string model)
    {
        var index = new SemanticAudioIndex(directions, clipsPerSong);
        var songs = repository.LoadSongs();
        repository.ForEachSongClips((id, clips) =>
        {
            if (songs.TryGetValue(id, out var song) && song.Model == model && !song.Failed)
                index.Put(song, clips);
        });
        return index;
    }

    // A song's row from all the clips it has: the selected ones, or - while a
    // raised setting is still being filled in - whichever of them exist, and
    // failing that every clip it has, so a song never drops out of searches
    // while it waits.
    public void Put(AnalysedSong song, IReadOnlyDictionary<int, float[]> clips)
    {
        if (clips.Count == 0)
        {
            Remove([song.TrackId]);
            return;
        }

        var stored = clips.Keys.ToHashSet();
        var selected = ClipGrid.Select(song.WindowCount, song.Duration, ClipsPerSong, stored).Where(stored.Contains).ToArray();
        if (selected.Length == 0)
            selected = clips.Keys.Order().ToArray();

        var vectors = selected.Select(k => clips[k]).ToArray();
        var mean = Embedding.Mean(vectors);
        var vocal = vectors.Max(v => Embedding.Dot(v, _directions.Vocal));
        var speech = Embedding.Dot(mean, _directions.Speech);

        _lock.EnterWriteLock();
        try
        {
            if (!_rows.TryGetValue(song.TrackId, out var row))
            {
                row = _count++;
                Grow(_count);
                _rows[song.TrackId] = row;
                _ids[row] = song.TrackId;
            }

            mean.CopyTo(_means, row * Clap.Dimensions);
            _vocal[row] = vocal;
            _speech[row] = speech;
            _selected[row] = selected;
        }
        finally
        {
            _lock.ExitWriteLock();
        }
    }

    public void Remove(IReadOnlyCollection<Guid> trackIds)
    {
        _lock.EnterWriteLock();
        try
        {
            foreach (var id in trackIds)
            {
                if (!_rows.Remove(id, out var row))
                    continue;

                // The last row moves into the hole, so the array stays dense.
                var last = --_count;
                if (row != last)
                {
                    _ids[row] = _ids[last];
                    Array.Copy(_means, last * Clap.Dimensions, _means, row * Clap.Dimensions, Clap.Dimensions);
                    _vocal[row] = _vocal[last];
                    _speech[row] = _speech[last];
                    _selected[row] = _selected[last];
                    _rows[_ids[row]] = row;
                }

                _selected[last] = [];
            }
        }
        finally
        {
            _lock.ExitWriteLock();
        }
    }

    public bool Contains(Guid trackId)
    {
        _lock.EnterReadLock();
        try
        {
            return _rows.ContainsKey(trackId);
        }
        finally
        {
            _lock.ExitReadLock();
        }
    }

    // A song's mean, copied out, or null when it hasn't been heard.
    public float[]? MeanOf(Guid trackId)
    {
        _lock.EnterReadLock();
        try
        {
            if (!_rows.TryGetValue(trackId, out var row))
                return null;

            return _means.AsSpan(row * Clap.Dimensions, Clap.Dimensions).ToArray();
        }
        finally
        {
            _lock.ExitReadLock();
        }
    }

    // One row of a search: the song's mean similarity to the prompt, its two
    // filter scores, and the clips its rerank reads.
    public readonly record struct Row(Guid TrackId, float Similarity, float VocalScore, float SpeechScore, int[] Selected);

    public Row[] Score(ReadOnlySpan<float> prompt)
    {
        _lock.EnterReadLock();
        try
        {
            var rows = new Row[_count];
            var means = _means.AsSpan();
            for (var row = 0; row < _count; row++)
            {
                var similarity = Embedding.Dot(means.Slice(row * Clap.Dimensions, Clap.Dimensions), prompt);
                rows[row] = new Row(_ids[row], similarity, _vocal[row], _speech[row], _selected[row]);
            }

            return rows;
        }
        finally
        {
            _lock.ExitReadLock();
        }
    }

    private void Grow(int needed)
    {
        if (needed <= _ids.Length)
            return;

        var capacity = Math.Max(needed, Math.Max(256, _ids.Length * 2));
        Array.Resize(ref _ids, capacity);
        Array.Resize(ref _means, capacity * Clap.Dimensions);
        Array.Resize(ref _vocal, capacity);
        Array.Resize(ref _speech, capacity);
        Array.Resize(ref _selected, capacity);
    }
}
