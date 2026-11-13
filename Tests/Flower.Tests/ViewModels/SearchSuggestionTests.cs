using Flower.Models;
using Flower.ViewModels;

namespace Flower.Tests.ViewModels;

public class SearchSuggestionTests
{
    [Fact]
    public void What_is_typed_narrows_the_suggestions_and_is_not_offered_back()
    {
        var suggestions = SearchSuggestion.For("nat",
            ["nat", "nat king cole", "radiohead", "the national"], [], SearchViewModel.MaxSuggestions);

        Assert.Equal(new[] { "nat king cole", "the national" }, suggestions.Select(s => s.Text));
    }

    // Opening Search is the start of a search, not a list of old ones.
    [Fact]
    public void An_empty_box_offers_nothing()
    {
        Assert.Empty(SearchSuggestion.For("", ["radiohead"], [], SearchViewModel.MaxSuggestions));
    }

    // After the past searches, the library: names that start with what is
    // typed before names with a later word that does, and each kind says
    // what it is.
    [Fact]
    public void The_library_s_names_are_offered_after_past_searches()
    {
        var tracks = new List<Track>
        {
            new() { Title = "Wildfire", Path = "/music/1.mp3", Album = "The Wild Hunt", Artists = "Tallest Man" },
            new() { Title = "Sonata", Path = "/music/2.mp3", Album = "Classics", Artists = "Wilco" },
        };
        var suggestions = SearchSuggestion.For("wil", ["wild nothing"], tracks, SearchViewModel.MaxSuggestions);

        Assert.Equal(
            new[]
            {
                ("wild nothing", SearchSuggestionKind.Recent),
                ("Wilco", SearchSuggestionKind.Artist),
                ("Wildfire", SearchSuggestionKind.Song),
                ("The Wild Hunt", SearchSuggestionKind.Album),
            },
            suggestions.Select(s => (s.Text, s.Kind)));
    }

    // Monk's albums and songs all begin with "Monk", his own name only has a
    // word that does - and the artist never made the five rows.
    [Fact]
    public void An_artist_is_offered_when_more_albums_and_songs_than_there_are_rows_match_better()
    {
        var tracks = new[] { "Monk's Dream", "Monk's Mood", "Monk's Music", "Monk's Point", "Monk's Blues", "Monkery" }
            .Select((name, i) => new Track
            {
                Title = name, Album = name, Artists = "Thelonious Monk", Path = $"/music/{i}.mp3",
            })
            .ToList();

        var suggestions = SearchSuggestion.For("monk", [], tracks, SearchViewModel.MaxSuggestions);

        Assert.Equal(SearchViewModel.MaxSuggestions, suggestions.Count);
        Assert.Contains(suggestions, s => s is { Text: "Thelonious Monk", Kind: SearchSuggestionKind.Artist });
        Assert.Contains(suggestions, s => s.Kind == SearchSuggestionKind.Album);
    }
}
