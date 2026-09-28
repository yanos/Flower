using System;
using System.Collections.Generic;

namespace Flower.Services;

// The letters down the side of mobile's alphabetical lists (ScrollIndexBar),
// and where in a list each one lands.
//
// A letter is worked out from the same text the list is sorted on, folded the
// way search folds it (SearchText.Fold), so "Émilie" files under E and "Øya"
// under O - one bar of 26 letters, whatever the library is written in. Anything
// that is not A-Z once folded (a digit, Greek, CJK) goes under #.
public static class AlphabetIndex
{
    public const char Other = '#';

    // # first, because that is where the lists put it: digits and punctuation
    // sort ahead of letters, so the # entries are at the top of the list and
    // the bar says so by putting # at the top of itself.
    public static IReadOnlyList<char> Letters { get; } = "#ABCDEFGHIJKLMNOPQRSTUVWXYZ".ToCharArray();

    // The same letters for a list sorted Z to A, where the # entries have gone
    // to the bottom.
    public static IReadOnlyList<char> LettersDescending { get; } = "ZYXWVUTSRQPONMLKJIHGFEDCBA#".ToCharArray();

    /// <summary>
    /// The letter <paramref name="text"/> is filed under.
    /// </summary>
    /// <param name="skipPunctuation">
    /// Whether the list's own sort ignores everything that is not a letter or a
    /// digit - true for the Songs list (TrackListBuilder.SortKey), which files
    /// "(What's the Story) Morning Glory?" under W. The album and artist lists
    /// sort the raw name, where that title sits among the # entries at the top,
    /// so they must not skip it or the jump lands somewhere the letter is not.
    /// </param>
    public static char LetterOf(string? text, bool skipPunctuation)
    {
        if (string.IsNullOrEmpty(text))
            return Other;

        foreach (var c in text)
        {
            if (char.IsWhiteSpace(c))
                continue;
            if (skipPunctuation && !char.IsLetterOrDigit(c))
                continue;

            var folded = char.ToUpperInvariant(SearchText.Fold(c));
            return folded is >= 'A' and <= 'Z' ? folded : Other;
        }

        return Other;
    }

    /// <summary>
    /// The index of the first item filed under <paramref name="letter"/>, or -
    /// when nothing is - under the nearest letter after it, so touching an
    /// empty letter still moves the list the way the finger moved. Past the
    /// last letter anything is filed under, that is the last item. -1 for an
    /// empty list. <paramref name="descending"/> for a list running Z to A,
    /// where "after" is further back in the alphabet.
    /// </summary>
    public static int FirstIndexFor<T>(IReadOnlyList<T> items, Func<T, char> letterOf, char letter, bool descending = false)
    {
        // Z to A is A to Z with the ranks turned round.
        int RankOf(char c) => descending ? -AlphabetIndex.RankOf(c) : AlphabetIndex.RankOf(c);
        var target = RankOf(letter);
        var best = -1;
        var bestRank = int.MaxValue;
        for (var i = 0; i < items.Count; i++)
        {
            var rank = RankOf(letterOf(items[i]));
            if (rank < target || rank >= bestRank)
                continue;

            best = i;
            bestRank = rank;
            if (rank == target)
                break;
        }

        if (best >= 0)
            return best;
        return items.Count - 1;
    }

    private static int RankOf(char letter) => letter is >= 'A' and <= 'Z' ? letter - 'A' + 1 : 0;
}
