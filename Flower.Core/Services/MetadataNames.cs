using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace Flower.Services;

// A name as every search by name in Flower compares it: Track Info's cover
// search (OnlineCoverArt) and the online lookups (OnlineMetadataEngine) both
// ask a service for something by the words in a tag, get back whatever shares
// a word with them, and have to tell which of those is the thing they asked
// for. One normaliser for both, here where the server can reach it too, so an
// album that matches in one matches in the other (docs/ONLINE-METADATA-PLAN.md,
// "Matching").
public static partial class MetadataNames
{
    // Lower case, accents off, the editions off (BareName), then nothing but
    // letters and digits: "Rumours (Super Deluxe)" and "rumours" are one
    // album, and so are "69 Love Songs Disc 1" and "69 Love Songs".
    public static string Normalize(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return "";

        var bare = BareName(text);
        var builder = new StringBuilder(bare.Length);
        foreach (var c in bare.Normalize(NormalizationForm.FormD))
        {
            if (CharUnicodeInfo.GetUnicodeCategory(c) == UnicodeCategory.NonSpacingMark)
                continue;
            if (char.IsLetterOrDigit(c))
                builder.Append(char.ToLowerInvariant(c));
        }
        return builder.ToString();
    }

    // A name without what a store or a rip adds to it that a library's tags
    // usually lack, or the other way round - "(Deluxe Edition)",
    // "[Remastered]", " - Single", "Disc 2". A name that is nothing but those -
    // "[Untitled]" - keeps its own words, out of their brackets, rather than
    // matching every other such name as nothing.
    public static string BareName(string text)
    {
        var bare = DiscSuffix().Replace(StoreSuffix().Replace(Bracketed().Replace(text, ""), ""), "").Trim();
        return bare.Length > 0 ? bare : Brackets().Replace(text, "").Trim();
    }

    // The first of a credit's artists: "Simon" for "Simon & Garfunkel",
    // "Daft Punk" for "Daft Punk, Pharrell Williams".
    public static string LeadArtist(string? artist)
    {
        if (string.IsNullOrWhiteSpace(artist))
            return "";

        var bare = Bracketed().Replace(artist, "");
        return ArtistSeparator().Split(bare).Select(part => part.Trim()).FirstOrDefault(part => part.Length > 0) ?? "";
    }

    [GeneratedRegex(@"\s*(?:[,;&/+]|\bfeat\b\.?|\bft\b\.?|\bfeaturing\b|\bwith\b|\band\b)\s*", RegexOptions.IgnoreCase)]
    private static partial Regex ArtistSeparator();

    [GeneratedRegex(@"[\s,:\-]*\b(?:Disc|Disk|CD)\s*\d+(?:\s*(?:of|/)\s*\d+)?\s*$", RegexOptions.IgnoreCase)]
    private static partial Regex DiscSuffix();

    [GeneratedRegex(@"[\(\)\[\]]")]
    private static partial Regex Brackets();

    [GeneratedRegex(@"\s*[\(\[][^\)\]]*[\)\]]")]
    private static partial Regex Bracketed();

    [GeneratedRegex(@"\s+-\s+(Single|EP)\s*$", RegexOptions.IgnoreCase)]
    private static partial Regex StoreSuffix();
}
