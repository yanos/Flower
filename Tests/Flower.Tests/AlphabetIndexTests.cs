using System.Collections.Generic;

using Flower.Services;

using Xunit;

namespace Flower.Tests;

// Which letter a name files under, and where a letter lands in a list - the
// part of the A-Z bar (ScrollIndexBar) that is plain logic.
public class AlphabetIndexTests
{
    [Theory]
    [InlineData("Abbey Road", 'A')]
    [InlineData("abbey road", 'A')]
    [InlineData("Émilie", 'E')]
    [InlineData("Øya", 'O')]
    [InlineData("1999", '#')]
    [InlineData("Ωmega", '#')]
    [InlineData("", '#')]
    [InlineData(null, '#')]
    [InlineData("  Leading space", 'L')]
    public void A_name_files_under_its_first_letter_folded(string? name, char expected) =>
        Assert.Equal(expected, AlphabetIndex.LetterOf(name, skipPunctuation: false));

    // The Songs list sorts with punctuation stripped, so its letter must skip
    // it too; the album and artist lists sort the raw name, where the same
    // title sits among the # entries.
    [Fact]
    public void Punctuation_is_skipped_only_when_the_sort_skips_it()
    {
        Assert.Equal('W', AlphabetIndex.LetterOf("(What's the Story) Morning Glory?", skipPunctuation: true));
        Assert.Equal('#', AlphabetIndex.LetterOf("(What's the Story) Morning Glory?", skipPunctuation: false));
    }

    private static readonly List<string> Names = ["1984", "Abba", "Air", "Beck", "Doves", "Muse"];

    private static int IndexOf(char letter) =>
        AlphabetIndex.FirstIndexFor(Names, n => AlphabetIndex.LetterOf(n, false), letter);

    [Fact]
    public void A_letter_lands_on_its_first_entry()
    {
        Assert.Equal(0, IndexOf('#'));
        Assert.Equal(1, IndexOf('A'));
        Assert.Equal(3, IndexOf('B'));
        Assert.Equal(5, IndexOf('M'));
    }

    [Fact]
    public void A_letter_with_nothing_under_it_lands_on_the_next_one_that_has_something()
    {
        Assert.Equal(4, IndexOf('C'));
        Assert.Equal(5, IndexOf('E'));
    }

    [Fact]
    public void Past_the_last_letter_in_use_lands_on_the_last_entry() =>
        Assert.Equal(5, IndexOf('Z'));

    // Z to A: a letter still lands on its first entry, an empty one on the
    // next letter down the list - which is the one before it in the alphabet -
    // and # is at the bottom.
    [Fact]
    public void A_list_sorted_z_to_a_lands_the_same_way_turned_round()
    {
        var reversed = Enumerable.Reverse(Names).ToList();
        int Reversed(char letter) =>
            AlphabetIndex.FirstIndexFor(reversed, n => AlphabetIndex.LetterOf(n, false), letter, descending: true);

        Assert.Equal(0, Reversed('M'));
        Assert.Equal(3, Reversed('A'));
        Assert.Equal(2, Reversed('C'));
        Assert.Equal(0, Reversed('Z'));
        Assert.Equal(5, Reversed('#'));
    }

    [Fact]
    public void An_empty_list_has_nowhere_to_land() =>
        Assert.Equal(-1, AlphabetIndex.FirstIndexFor(new List<string>(), n => AlphabetIndex.LetterOf(n, false), 'A'));
}
