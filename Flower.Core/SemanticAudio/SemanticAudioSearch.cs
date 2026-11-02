using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;

using Flower.Models;

namespace Flower.SemanticAudio;

public sealed record SemanticAudioQuery(string Prompt, bool Instrumental = false);

public readonly record struct SemanticAudioMatch(Guid TrackId, float Score);

// A prompt to the library's songs that sound like it, best first
// (docs/VIBE-PLAYLIST-PLAN.md, "Search"): encode the prompt, compare it with
// every song's mean, filter, and rerank the best few hundred with their clips.
public sealed class SemanticAudioSearch(SemanticAudioIndex index, SemanticAudioRepository repository, IClapTextEncoder encoder)
{
    // How many of the mean ranking are reranked with their clips, which is
    // also as far down as a ranking goes.
    public const int Candidates = 500;

    // score = Alpha * mean similarity + (1 - Alpha) * the mean of the two best
    // clip similarities. The clips find a song that is half quiet intro and
    // half orchestra, which an average blurs; phase 0 found little between 0,
    // 0.5 and 1, and took the middle.
    public const float Alpha = 0.5f;

    // How many songs Similar answers with: a device draws its next few from
    // them (VibeQueueSource), so they have to be enough to keep albums and
    // artists apart, and few enough to stay close to the songs asked about.
    public const int SimilarCandidates = 50;

    private const int CacheSize = 256;

    // A preview runs on every pause in typing, and the encode is the only part
    // that costs: about 160 ms on one thread.
    private readonly ConcurrentDictionary<string, float[]> _prompts = new(StringComparer.Ordinal);

    public SemanticAudioIndex Index => index;

    public IReadOnlyList<SemanticAudioMatch> Rank(SemanticAudioQuery query, Func<Guid, Track?> track, int limit = Candidates)
    {
        var prompt = SemanticAudioFilters.Parse(query.Prompt);
        var instrumental = query.Instrumental || prompt.AsksInstrumental;
        var text = Encode(string.Format(Clap.PromptTemplate, prompt.Text));

        var kept = new List<SemanticAudioIndex.Row>();
        foreach (var row in index.Score(text))
        {
            if (track(row.TrackId) is not { } song)
                continue;

            if (row.SpeechScore > SemanticAudioFilters.SpeechAbove || SemanticAudioFilters.IsTaggedSpeech(song))
                continue;

            // Christmas songs only when asked for, and then only them.
            if (SemanticAudioFilters.IsTaggedChristmas(song) != prompt.AsksChristmas)
                continue;

            if (instrumental && row.VocalScore >= SemanticAudioFilters.InstrumentalBelow)
                continue;

            kept.Add(row);
        }

        var candidates = kept.OrderByDescending(r => r.Similarity).Take(Math.Min(limit, Candidates)).ToList();
        var clips = repository.LoadClips(candidates.Select(c => c.TrackId).ToList());

        var ranked = new List<SemanticAudioMatch>(candidates.Count);
        foreach (var candidate in candidates)
        {
            var bestTwo = clips.TryGetValue(candidate.TrackId, out var songClips)
                ? BestTwo(candidate.Selected, songClips, text) ?? candidate.Similarity
                : candidate.Similarity;
            ranked.Add(new SemanticAudioMatch(candidate.TrackId, Alpha * candidate.Similarity + (1 - Alpha) * bestTwo));
        }

        ranked.Sort((a, b) => b.Score.CompareTo(a.Score));
        return ranked;
    }

    // Endless Play (docs/VIBE-PLAYLIST-PLAN.md, "Endless Play"): the songs
    // nearest the seeds' songs taken together - the normalised average of
    // their means, so a run of songs pulls toward what they share rather
    // than toward any one of them. Song against song, with no sentence to
    // encode, so no rerank by clips either: two whole songs' means compare
    // like for like. The filters follow the seeds: Christmas songs only
    // after a Christmas song, and instrumentals only after a run of nothing
    // else. Spoken word never, as in Rank.
    public IReadOnlyList<SemanticAudioMatch> Similar(
        IReadOnlyList<Guid> seeds, IReadOnlySet<Guid> exclude, Func<Guid, Track?> track, int limit = SimilarCandidates)
    {
        var vectors = seeds.Distinct().Select(index.MeanOf).OfType<float[]>().ToArray();
        if (vectors.Length == 0)
            return [];

        var centre = Embedding.Mean(vectors);
        var seedIds = seeds.ToHashSet();
        var rows = index.Score(centre);

        var heard = rows.Where(r => seedIds.Contains(r.TrackId)).ToList();
        var instrumental = heard.Count > 0 && heard.All(r => r.VocalScore < SemanticAudioFilters.InstrumentalBelow);
        var christmas = seedIds.Any(id => track(id) is { } seed && SemanticAudioFilters.IsTaggedChristmas(seed));

        var kept = new List<SemanticAudioMatch>();
        foreach (var row in rows)
        {
            if (seedIds.Contains(row.TrackId) || exclude.Contains(row.TrackId))
                continue;
            if (track(row.TrackId) is not { } song)
                continue;
            if (row.SpeechScore > SemanticAudioFilters.SpeechAbove || SemanticAudioFilters.IsTaggedSpeech(song))
                continue;
            if (!christmas && SemanticAudioFilters.IsTaggedChristmas(song))
                continue;
            if (instrumental && row.VocalScore >= SemanticAudioFilters.InstrumentalBelow)
                continue;

            kept.Add(new SemanticAudioMatch(row.TrackId, row.Similarity));
        }

        kept.Sort((a, b) => b.Score.CompareTo(a.Score));
        return kept.Count > limit ? kept.GetRange(0, limit) : kept;
    }

    // The mean of a song's two best clip similarities; its one clip twice
    // over when it has only one, and null when its clips are gone (the song
    // went stale between the scoring and the read), so the mean stands in.
    private static float? BestTwo(int[] selected, Dictionary<int, float[]> clips, float[] text)
    {
        float best = float.NegativeInfinity, second = float.NegativeInfinity;
        foreach (var window in selected)
        {
            if (!clips.TryGetValue(window, out var clip))
                continue;

            var similarity = Embedding.Dot(clip, text);
            if (similarity > best)
            {
                second = best;
                best = similarity;
            }
            else if (similarity > second)
            {
                second = similarity;
            }
        }

        if (float.IsNegativeInfinity(best))
            return null;

        return float.IsNegativeInfinity(second) ? best : (best + second) / 2;
    }

    private float[] Encode(string text)
    {
        if (_prompts.TryGetValue(text, out var cached))
            return cached;

        var vector = encoder.Encode([text])[0];
        if (_prompts.Count >= CacheSize)
            _prompts.Clear();
        _prompts[text] = vector;
        return vector;
    }
}
