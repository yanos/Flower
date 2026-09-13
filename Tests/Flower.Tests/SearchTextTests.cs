using Flower.Services;

using Xunit;

namespace Flower.Tests;

// Search that matches the way someone typing expects. The names below are real
// artist and album spellings, because that is where this actually bites: a
// library full of "Motörhead" and "Björk" is an ordinary library, and typing
// the umlaut is the part nobody does.
public class SearchTextTests
{
    [Theory]
    [InlineData("Café Bleu", "cafe")]
    [InlineData("Björk", "bjork")]
    [InlineData("Motörhead", "motorhead")]
    [InlineData("Sigur Rós", "ros")]
    [InlineData("Beyoncé", "beyonce")]
    [InlineData("Mötley Crüe", "motley crue")]
    [InlineData("Antônio Carlos Jobim", "antonio")]
    [InlineData("Niño", "nino")]
    [InlineData("Dvořák", "dvorak")]
    [InlineData("Łódź", "lodz")]
    [InlineData("Þorir", "torir")]
    public void A_name_is_found_without_typing_its_accents(string value, string query) =>
        Assert.True(SearchText.Contains(value, query));

    // The other direction, which matters just as much: someone who *does* type
    // the accent - pasting a name, or on a keyboard that has the key - must not
    // get fewer results than someone who did not.
    [Theory]
    [InlineData("Cafe Bleu", "café")]
    [InlineData("Bjork", "björk")]
    [InlineData("Motorhead", "Motörhead")]
    public void An_accented_query_still_finds_the_unaccented_spelling(string value, string query) =>
        Assert.True(SearchText.Contains(value, query));

    // Letters that are not a base plus a combining mark, so the framework's
    // IgnoreNonSpace would miss every one of these. This is the case that
    // decided against leaning on CompareInfo even where it is available.
    [Theory]
    [InlineData("Løud", "loud")]
    [InlineData("Đorđe", "dorde")]
    [InlineData("Straße", "strase")]
    [InlineData("Æther", "ather")]
    public void Letters_that_are_not_an_accented_base_fold_too(string value, string query) =>
        Assert.True(SearchText.Contains(value, query));

    // The price of a fold that never allocates: one char in, one char out, so
    // the ligatures fold to their first letter rather than expanding. "ß" is
    // "s" and not "ss", "æ" is "a" and not "ae". Pinned rather than merely
    // commented, because it is the one place the matcher is deliberately less
    // helpful than a person might expect - if someone later widens the fold to
    // expand these, this is the test that should make them do it on purpose.
    [Theory]
    [InlineData("Straße", "strasse")]
    [InlineData("Æther", "aether")]
    public void A_ligature_folds_to_one_letter_not_two(string value, string query) =>
        Assert.False(SearchText.Contains(value, query));

    [Theory]
    [InlineData("Café", "tea")]
    [InlineData("Björk", "bjorn")]
    [InlineData("abc", "abcd")]      // query longer than the value
    [InlineData("Café", "")]         // an empty query matches nothing, not everything
    public void Something_that_does_not_match_still_does_not(string value, string query) =>
        Assert.False(SearchText.Contains(value, query));

    [Fact]
    public void A_null_on_either_side_is_not_a_match()
    {
        Assert.False(SearchText.Contains(null, "cafe"));
        Assert.False(SearchText.Contains("Café", null));
    }

    // Scripts the fold table says nothing about must still find themselves -
    // falling through unchanged is the whole contract for them.
    [Theory]
    [InlineData("太郎の歌", "太郎")]
    [InlineData("Москва", "москв")]
    [InlineData("Ελλάδα", "Ελλ")]
    public void A_script_the_table_does_not_cover_still_matches_itself(string value, string query) =>
        Assert.True(SearchText.Contains(value, query));

    // Matching is a substring search, not a prefix one - "getCoverArt" style
    // ids aside, people search for a word in the middle of a title constantly.
    [Fact]
    public void A_match_can_start_anywhere()
    {
        Assert.True(SearchText.Contains("Live at the Café de Paris", "de paris"));
        Assert.True(SearchText.Contains("Live at the Café de Paris", "café"));
    }
}
