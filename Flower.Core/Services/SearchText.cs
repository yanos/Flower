using System;
using System.Text;

namespace Flower.Services;

// Matching text the way someone typing into a search box expects it to match:
// "cafe" finds "Café", "bjork" finds "Björk", "motorhead" finds "Motörhead".
// Accents are how the name is spelled, not how it is typed - a keyboard that
// can produce "ö" without effort is the exception, and the track is in the
// library either way.
//
// Why this is hand-rolled rather than a call into the framework, for two
// reasons that outlive any one project's settings.
//
// It folds further than the framework can. CompareInfo.IndexOf with
// CompareOptions.IgnoreNonSpace - the obvious answer - drops combining marks
// and nothing else, so it never matches "ø", "ł", "đ" or "ß": those are
// letters in their own right, not a base plus an accent. "Løud" is exactly
// the kind of name this is for.
//
// And it gives every head the same answer. The framework's Unicode behaviour
// depends on whether the process has ICU, which is a per-project build switch
// (InvariantGlobalization) and, on the browser and mobile heads, a size
// decision someone may well make later. Both framework answers fail *silently*
// without ICU rather than throwing - IgnoreNonSpace simply finds nothing, and
// string.Normalize returns its input while IsNormalized reports success - so
// the failure mode is not an exception but a phone and a server disagreeing
// about what matches. That is the same shape of bug as two mDNS backends
// keying one server differently. This has no such dependency.
public static class SearchText
{
    // True if value contains query, ignoring case and accents.
    //
    // The fold is per-character and allocation-free: most libraries are mostly
    // ASCII, and this runs over every field of every track on each keystroke.
    public static bool Contains(string? value, string? query)
    {
        if (string.IsNullOrEmpty(query))
            return false;
        if (string.IsNullOrEmpty(value) || value.Length < query.Length)
            return false;

        // Ordinal is the right comparison *after* folding, because folding is
        // what the culture-sensitive comparison would have been doing.
        for (var start = 0; start <= value.Length - query.Length; start++)
        {
            if (MatchesAt(value, query, start))
                return true;
        }

        return false;
    }

    private static bool MatchesAt(string value, string query, int start)
    {
        for (var i = 0; i < query.Length; i++)
        {
            if (Fold(value[start + i]) != Fold(query[i]))
                return false;
        }

        return true;
    }

    // One character, lowercased and stripped of its accent.
    //
    // Covers Latin-1 Supplement and Latin Extended-A, which between them hold
    // the accented Latin letters that turn up in music metadata - French,
    // German, Spanish, Portuguese, Nordic, Polish, Czech, Turkish, Baltic.
    // Anything outside that (Greek, Cyrillic, CJK) falls through unchanged and
    // still matches itself exactly, which is the behaviour it had before.
    //
    // Deliberately not expanding one char into several: "ß" folds to "s", not
    // "ss", and "æ" to "a", so that this stays a same-length comparison. A
    // query of "strasse" will not find "Straße"; "strase" will. That is the
    // trade for a matcher that does not allocate.
    public static char Fold(char c)
    {
        if (c < 0x80)
            return char.ToLowerInvariant(c);

        var lower = char.ToLowerInvariant(c);
        return lower switch
        {
            'à' or 'á' or 'â' or 'ã' or 'ä' or 'å' or 'ā' or 'ă' or 'ą' => 'a',
            'æ' => 'a',
            'ç' or 'ć' or 'ĉ' or 'ċ' or 'č' => 'c',
            'ď' or 'đ' => 'd',
            'è' or 'é' or 'ê' or 'ë' or 'ē' or 'ĕ' or 'ė' or 'ę' or 'ě' => 'e',
            'ĝ' or 'ğ' or 'ġ' or 'ģ' => 'g',
            'ĥ' or 'ħ' => 'h',
            'ì' or 'í' or 'î' or 'ï' or 'ĩ' or 'ī' or 'ĭ' or 'į' or 'ı' => 'i',
            'ĵ' => 'j',
            'ķ' or 'ĸ' => 'k',
            'ĺ' or 'ļ' or 'ľ' or 'ŀ' or 'ł' => 'l',
            'ñ' or 'ń' or 'ņ' or 'ň' or 'ŉ' or 'ŋ' => 'n',
            'ò' or 'ó' or 'ô' or 'õ' or 'ö' or 'ø' or 'ō' or 'ŏ' or 'ő' => 'o',
            'œ' => 'o',
            'ŕ' or 'ŗ' or 'ř' => 'r',
            'ś' or 'ŝ' or 'ş' or 'š' or 'ß' => 's',
            'ţ' or 'ť' or 'ŧ' => 't',
            'ù' or 'ú' or 'û' or 'ü' or 'ũ' or 'ū' or 'ŭ' or 'ů' or 'ű' or 'ų' => 'u',
            'ŵ' => 'w',
            'ý' or 'ÿ' or 'ŷ' => 'y',
            'ź' or 'ż' or 'ž' => 'z',
            'ð' => 'd',
            'þ' => 't',
            _ => lower,
        };
    }
}
