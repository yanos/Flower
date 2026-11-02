using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;

using Flower.Models;

namespace Flower.SemanticAudio;

// A prompt as the search uses it: the words CLAP is asked about, and the two
// things the prompt said that are not about sound.
public sealed record SemanticAudioPrompt(string Text, bool AsksInstrumental, bool AsksChristmas);

// What a prompt can't tell CLAP (docs/VIBE-PLAYLIST-PLAN.md, "Filters"). Each
// is applied to the ranking, never written into the prompt: phase 0 found
// that words like "without lyrics" in the prompt dilute what was asked rather
// than being obeyed.
public static partial class SemanticAudioFilters
{
    // Phase 0's (vocals.py), and the cut-off was set against them: changing a
    // phrase moves every score.
    public static readonly IReadOnlyList<string> VocalPhrases =
        ["This audio is a song with a singer.", "This audio is music with vocals.", "This audio is someone singing lyrics."];

    public static readonly IReadOnlyList<string> InstrumentalPhrases =
        ["This audio is instrumental music.", "This audio is music without vocals.", "This audio is an instrumental track with no singing."];

    public static readonly IReadOnlyList<string> SpeechPhrases =
        ["This audio is people talking.", "This audio is a comedy sketch.", "This audio is a man speaking.", "This audio is spoken word with laughter."];

    public static readonly IReadOnlyList<string> MusicPhrases =
        ["This audio is music.", "This audio is a song.", "This audio is instrumental music.", "This audio is a song with singing."];

    // A song counts as instrumental below this vocal score. Set in phase 0
    // from 60 songs the owner labelled by ear: every one below -0.14 was
    // instrumental. It keeps about 60% of the library's instrumentals and lets
    // almost no sung song through, which is the right mistake for "no lyrics".
    public const float InstrumentalBelow = -0.15f;

    // A song is left out of every ranking above this speech score: the 17
    // highest in phase 0's sample were all comedy sketches.
    public const float SpeechAbove = 0f;

    // Turns a prompt into what is encoded. "Instrumental", "without lyrics" and
    // the like set the No vocals switch and are cut out; Christmas is noticed
    // and left in, since it is a sound too.
    public static SemanticAudioPrompt Parse(string prompt)
    {
        var asksInstrumental = InstrumentalWords().IsMatch(prompt);
        var text = asksInstrumental ? InstrumentalWords().Replace(prompt, " ") : prompt;
        text = Tidy(text);
        if (text.Length == 0)
            text = asksInstrumental ? "instrumental music" : "music";

        return new SemanticAudioPrompt(text, asksInstrumental, ChristmasWords().IsMatch(prompt));
    }

    // A comedy sketch can sound like whatever is asked for, and audio alone
    // caught only about half of the sample's: one with music under it scores as
    // music. The genre tag catches most of the rest.
    public static bool IsTaggedSpeech(Track track) =>
        track.Genre is { Length: > 0 } genre && SpeechGenres().IsMatch(genre);

    // CLAP doesn't know what Christmas music is - "White Christmas" ranked
    // 113th to 485th of 1,000 for "christmas music" - so it is read from tags.
    public static bool IsTaggedChristmas(Track track) =>
        Names(track.Title) || Names(track.Album) || Names(track.Genre);

    private static bool Names(string? tag) => tag is { Length: > 0 } && ChristmasWords().IsMatch(tag);

    // The parts the cut leaves dangling: "jazz, no vocals, for the evening"
    // keeps one comma, "chill music with no vocals" loses its "with", and
    // "Rainy Day Music (Instrumental)" its brackets.
    private static string Tidy(string text)
    {
        text = EmptyBrackets().Replace(text, " ");
        text = Spaces().Replace(text, " ");
        text = SpaceBeforePunctuation().Replace(text, "$1");
        text = RepeatedPunctuation().Replace(text, "$1");
        string before;
        do
        {
            before = text;
            text = text.Trim(',', ';', ':', '-', '–', '—', '.', ' ');
            text = EdgeJoin().Replace(text, "");
        }
        while (text != before);

        return text;
    }

    [GeneratedRegex(@"\b(?:instrumentals?|without (?:any )?(?:lyrics|vocals?|singing|singers?|voices?|words)|no (?:lyrics|vocals?|singing|singers?|voices?|words)|sans (?:paroles|voix|chant)|lyric-?less|non-vocal|vocal-?free)\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex InstrumentalWords();

    [GeneratedRegex(@"\b(?:christmas|xmas|x-mas|noël|navidad|weihnacht\w*|natale|santa claus|jingle bells?|silent night|sleigh ride|rudolph|frosty the snowman|holy night|deck the halls|mistletoe)\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex ChristmasWords();

    [GeneratedRegex(@"\b(?:comedy|humou?r|spoken(?: word)?|stand-?up)\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex SpeechGenres();

    [GeneratedRegex(@"^(?:and|or|but|with)\s+|\s+(?:and|or|but|with)$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex EdgeJoin();

    [GeneratedRegex(@"\(\s*\)|\[\s*\]")]
    private static partial Regex EmptyBrackets();

    [GeneratedRegex(@"\s+([,;])")]
    private static partial Regex SpaceBeforePunctuation();

    [GeneratedRegex(@"([,;])(?:\s*[,;])+")]
    private static partial Regex RepeatedPunctuation();

    [GeneratedRegex(@"\s+")]
    private static partial Regex Spaces();
}

// The two filter directions, from the text encoder: a clip's vocal score is
// its similarity to the vocal phrases minus its similarity to the
// instrumental ones, which is one dot product with the difference of their
// means; the speech score likewise.
public sealed record FilterDirections(float[] Vocal, float[] Speech)
{
    public static FilterDirections From(IClapTextEncoder encoder) => new(
        Difference(encoder, SemanticAudioFilters.VocalPhrases, SemanticAudioFilters.InstrumentalPhrases),
        Difference(encoder, SemanticAudioFilters.SpeechPhrases, SemanticAudioFilters.MusicPhrases));

    private static float[] Difference(IClapTextEncoder encoder, IReadOnlyList<string> toward, IReadOnlyList<string> away)
    {
        var plus = MeanOf(encoder.Encode(toward));
        var minus = MeanOf(encoder.Encode(away));
        for (var i = 0; i < plus.Length; i++)
            plus[i] -= minus[i];
        return plus;
    }

    // Unlike a song's mean, not renormalised: phase 0 took the plain average
    // of the unit phrase vectors, and the cut-offs were measured on that.
    private static float[] MeanOf(float[][] vectors)
    {
        var mean = new float[Clap.Dimensions];
        foreach (var vector in vectors)
        {
            for (var i = 0; i < mean.Length; i++)
                mean[i] += vector[i] / vectors.Length;
        }

        return mean;
    }
}
