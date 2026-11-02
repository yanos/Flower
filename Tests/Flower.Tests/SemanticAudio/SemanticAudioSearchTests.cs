using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

using Flower.Models;
using Flower.Persistence.Sql;
using Flower.SemanticAudio;

using Microsoft.Data.Sqlite;

using Xunit;

using static Flower.Tests.SemanticAudio.FakeClapTextEncoder;

namespace Flower.Tests.SemanticAudio;

// Ranking a library made of chosen vectors (docs/VIBE-PLAYLIST-PLAN.md,
// "Search" and "Filters"). The prompt is axis 0; how much of axis 0 a clip
// holds is how well it fits.
public class SemanticAudioSearchTests : IDisposable
{
    private const string Model = "music_audioset_epoch_15_esc_90.14/int8";
    private const string Prompt = "music for a rainy day";

    private readonly string _directory = Path.Combine(Path.GetTempPath(), "flower-semantic-search-" + Guid.NewGuid().ToString("N"));
    private readonly SemanticAudioRepository _repository;
    private readonly FakeClapTextEncoder _text = new();
    private readonly Dictionary<Guid, Track> _library = new();

    public SemanticAudioSearchTests()
    {
        Directory.CreateDirectory(_directory);
        _repository = new SemanticAudioRepository(new FlowerDb(Path.Combine(_directory, "flower.db")));
        _text.SetPrompt(Prompt, Axis(0));
        _text.SetPrompt("sad piano", Axis(0));
        _text.SetPrompt("christmas music", Axis(0));
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try
        {
            Directory.Delete(_directory, recursive: true);
        }
        catch (IOException)
        {
            // A pooled handle outliving the test is not what is under test.
        }
    }

    // A song whose windows 0, 1, ... are the clips given.
    private Track Add(string title, float[][] clips, string? genre = null, string? album = null)
    {
        var track = new Track { Title = title, Genre = genre, Album = album };
        _library[track.Id] = track;
        var song = new AnalysedSong(track.Id, Model, "1", TimeSpan.FromSeconds(clips.Length * 10), clips.Length,
            DateTimeOffset.UtcNow, null, Enumerable.Range(0, clips.Length).ToHashSet());
        _repository.SaveSong(song, clips.Select((c, i) => (i, c)).ToDictionary(p => p.i, p => p.c), replace: false);
        return track;
    }

    private SemanticAudioSearch Search() => new(
        SemanticAudioIndex.Build(_repository, FilterDirections.From(_text), ClipGrid.WholeSong, Model),
        _repository,
        _text);

    private List<string?> Rank(string prompt, bool instrumental = false) =>
        Search().Rank(new SemanticAudioQuery(prompt, instrumental), id => _library.GetValueOrDefault(id))
            .Select(m => _library[m.TrackId].Title)
            .ToList();

    [Fact]
    public void Ranks_songs_by_how_well_they_fit()
    {
        Add("poor", [Mix((0, 0.2f), (1, 1)), Mix((0, 0.2f), (1, 1))]);
        Add("good", [Mix((0, 1), (1, 0.3f)), Mix((0, 1), (1, 0.3f))]);
        Add("fair", [Mix((0, 0.6f), (1, 0.8f)), Mix((0, 0.6f), (1, 0.8f))]);

        Assert.Equal(["good", "fair", "poor"], Rank(Prompt));
    }

    [Fact]
    public void Two_clips_that_fit_lift_a_song_its_average_hides()
    {
        // Steady: every clip fits fairly - mean 0.8, best two 0.8, score 0.8.
        // Changing: two of six clips fit exactly - mean 0.71, best two 1,
        // score 0.85. The mean alone would put steady first.
        Add("steady", Enumerable.Repeat(Mix((0, 0.8f), (1, 0.6f)), 6).ToArray());
        Add("changing", [Axis(0), Axis(0), Axis(5), Axis(6), Axis(7), Axis(8)]);

        Assert.Equal(["changing", "steady"], Rank(Prompt));
    }

    [Fact]
    public void Leaves_out_what_sounds_like_people_talking_or_is_tagged_comedy()
    {
        Add("song", [Mix((0, 1), (MusicAxis, 0.5f))]);
        Add("sketch", [Mix((0, 1), (SpeechAxis, 0.5f))]);
        Add("tagged sketch", [Mix((0, 1), (MusicAxis, 0.5f))], genre: "Comedy");

        Assert.Equal(["song"], Rank(Prompt));
    }

    [Fact]
    public void Without_singing_keeps_only_songs_no_voice_is_heard_in()
    {
        Add("sung", [Mix((0, 1), (InstrumentalAxis, 0.4f)), Mix((0, 1), (VocalAxis, 0.5f))]);
        Add("instrumental", [Mix((0, 1), (InstrumentalAxis, 0.4f)), Mix((0, 1), (InstrumentalAxis, 0.4f))]);

        Assert.Equal(["instrumental"], Rank("sad piano without singing"));
        Assert.Equal(["instrumental"], Rank(Prompt, instrumental: true));
        Assert.Equal(2, Rank(Prompt).Count);
        Assert.DoesNotContain(_text.Encoded, t => t.Contains("singing", StringComparison.Ordinal) && t.StartsWith("A song", StringComparison.Ordinal));
    }

    [Fact]
    public void Christmas_songs_play_only_when_asked_for_and_then_alone()
    {
        Add("song", [Axis(0)]);
        Add("White Christmas", [Mix((0, 0.5f), (1, 1))]);

        Assert.Equal(["song"], Rank(Prompt));
        Assert.Equal(["White Christmas"], Rank("christmas music"));
    }

    [Fact]
    public void A_song_no_longer_in_the_library_is_not_ranked()
    {
        var gone = Add("gone", [Axis(0)]);
        Add("kept", [Mix((0, 0.5f), (1, 1))]);
        _library.Remove(gone.Id);

        var ranked = Search().Rank(new SemanticAudioQuery(Prompt), id => _library.GetValueOrDefault(id));

        Assert.DoesNotContain(ranked, m => m.TrackId == gone.Id);
    }

    [Fact]
    public void Encodes_a_prompt_once()
    {
        Add("song", [Axis(0)]);
        var search = Search();
        _text.Encoded.Clear();

        search.Rank(new SemanticAudioQuery(Prompt), id => _library.GetValueOrDefault(id));
        search.Rank(new SemanticAudioQuery(Prompt), id => _library.GetValueOrDefault(id));

        Assert.Equal([string.Format(Clap.PromptTemplate, Prompt)], _text.Encoded);
    }
}
