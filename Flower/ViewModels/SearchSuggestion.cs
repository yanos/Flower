using System;
using System.Collections.Generic;
using System.Linq;

using Material.Icons;

using Flower.Models;
using Flower.Services;

namespace Flower.ViewModels;

public enum SearchSuggestionKind { Recent, Artist, Album, Song }

/// <summary>
/// One row of the Search tab's dropdown: something the words typed so far
/// could be finishing - a past search, or the name of an artist, an album or
/// a song in the library. Picking it puts <see cref="Text"/> in the box.
/// <see cref="Detail"/> says whose an album or a song is.
/// </summary>
public sealed record SearchSuggestion(string Text, SearchSuggestionKind Kind, string? Detail = null)
{
    public MaterialIconKind Icon => Kind switch
    {
        SearchSuggestionKind.Recent => MaterialIconKind.History,
        SearchSuggestionKind.Artist => MaterialIconKind.AccountMusicOutline,
        SearchSuggestionKind.Album => MaterialIconKind.Album,
        _ => MaterialIconKind.MusicNote,
    };

    public bool HasDetail => !string.IsNullOrEmpty(Detail);

    // Past searches go first, being what this person has looked for before,
    // but no more than this many: the rest of the room is the library's.
    private const int MaxRecent = 2;

    /// <summary>
    /// What to suggest for <paramref name="typed"/>: nothing while nothing is
    /// typed - an empty box is the start of a search, not a list of old ones -
    /// and otherwise past searches, then artists, albums and songs, whose
    /// names begin with it or have a word that does (SearchText.WordStartMatch).
    /// Names that begin with it come before names that only have a word that
    /// does, and what is typed is never offered back to itself.
    /// </summary>
    public static IReadOnlyList<SearchSuggestion> For(string? typed, IEnumerable<string> recentSearches,
        IEnumerable<Track> library, int max)
    {
        var query = typed?.Trim() ?? "";
        if (query.Length == 0 || max <= 0)
            return [];

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { query };
        var suggestions = new List<SearchSuggestion>(max);

        foreach (var recent in recentSearches)
        {
            if (suggestions.Count >= Math.Min(MaxRecent, max))
                break;
            if (SearchText.WordStartMatch(recent, query) >= 0 && seen.Add(recent))
                suggestions.Add(new SearchSuggestion(recent, SearchSuggestionKind.Recent));
        }

        // Each name once, with how well it matched and how often its songs
        // have been played, which decides between two equally good matches.
        var found = new Dictionary<(SearchSuggestionKind, string), (SearchSuggestion Suggestion, int Score, int Plays)>();
        void Consider(string? name, SearchSuggestionKind kind, string? detail, int plays)
        {
            if (string.IsNullOrWhiteSpace(name) || SearchText.WordStartMatch(name, query) is not (var score and >= 0))
                return;
            var key = (kind, name.ToLowerInvariant());
            found[key] = found.TryGetValue(key, out var known)
                ? known with { Plays = known.Plays + plays }
                : (new SearchSuggestion(name, kind, detail), score, plays);
        }

        foreach (var track in library)
        {
            var plays = track.TotalPlayCount;
            Consider(track.Artists, SearchSuggestionKind.Artist, null, plays);
            Consider(track.Album, SearchSuggestionKind.Album, track.EffectiveAlbumArtist, plays);
            Consider(track.Title, SearchSuggestionKind.Song, track.Artists, plays);
        }

        foreach (var (suggestion, _, _) in found.Values
                     .OrderBy(f => f.Score)
                     .ThenBy(f => f.Suggestion.Kind)
                     .ThenByDescending(f => f.Plays)
                     .ThenBy(f => f.Suggestion.Text.Length))
        {
            if (suggestions.Count >= max)
                break;
            if (seen.Add(suggestion.Text))
                suggestions.Add(suggestion);
        }

        return suggestions;
    }
}
