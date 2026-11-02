using Flower.Models;
using Flower.SemanticAudio;

using Xunit;

namespace Flower.Tests.SemanticAudio;

// What a prompt says that isn't about sound, and the tag rules
// (docs/VIBE-PLAYLIST-PLAN.md, "Filters").
public class SemanticAudioFiltersTests
{
    [Theory]
    [InlineData("sad piano without singing", "sad piano")]
    [InlineData("instrumental jazz for the evening", "jazz for the evening")]
    [InlineData("chill music with no vocals", "chill music")]
    [InlineData("jazz, no vocals, for the evening", "jazz, for the evening")]
    [InlineData("musique sans paroles", "musique")]
    [InlineData("Rainy Day Music (Instrumental)", "Rainy Day Music")]
    [InlineData("instrumental", "instrumental music")]
    public void Takes_asking_for_no_vocals_out_of_the_prompt(string prompt, string encoded)
    {
        var parsed = SemanticAudioFilters.Parse(prompt);

        Assert.True(parsed.AsksInstrumental);
        Assert.Equal(encoded, parsed.Text);
    }

    [Theory]
    [InlineData("music for a rainy day")]
    [InlineData("songs with a great vocalist")]
    [InlineData("no-wave guitars")]
    public void Leaves_a_prompt_about_sound_alone(string prompt)
    {
        var parsed = SemanticAudioFilters.Parse(prompt);

        Assert.False(parsed.AsksInstrumental);
        Assert.Equal(prompt, parsed.Text);
    }

    [Theory]
    [InlineData("christmas music", true)]
    [InlineData("cosy Xmas jazz", true)]
    [InlineData("music for a rainy day", false)]
    public void Notices_a_prompt_naming_christmas_and_keeps_the_word(string prompt, bool christmas)
    {
        var parsed = SemanticAudioFilters.Parse(prompt);

        Assert.Equal(christmas, parsed.AsksChristmas);
        Assert.Equal(prompt, parsed.Text);
    }

    [Theory]
    [InlineData("White Christmas", null, null, true)]
    [InlineData("Linus and Lucy", "A Charlie Brown Christmas", null, true)]
    [InlineData("Jingle Bell Rock", null, null, true)]
    [InlineData("Santa Fe", "Rent", null, false)]
    [InlineData("Song", "Album", "Holiday; Christmas", true)]
    public void Reads_christmas_from_title_album_or_genre(string title, string? album, string? genre, bool christmas)
    {
        var track = new Track { Title = title, Album = album, Genre = genre };

        Assert.Equal(christmas, SemanticAudioFilters.IsTaggedChristmas(track));
    }

    [Theory]
    [InlineData("Comedy", true)]
    [InlineData("Humour", true)]
    [InlineData("Spoken Word", true)]
    [InlineData("Rock", false)]
    [InlineData(null, false)]
    public void Reads_a_sketch_from_its_genre(string? genre, bool speech)
    {
        Assert.Equal(speech, SemanticAudioFilters.IsTaggedSpeech(new Track { Genre = genre }));
    }
}
