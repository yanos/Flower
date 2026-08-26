using Avalonia;
using Avalonia.Media;
using Avalonia.Media.Fonts;

namespace Flower;

// The app-wide font setup, shared by all four entry points (Desktop, Android,
// iOS, Web) so the fallback list cannot drift between platforms - every one of
// them used to call .WithInterFont() directly and nothing else.
//
// Inter is the UI font, and it contains no CJK glyphs at all. Anything outside
// its coverage is therefore resolved by the platform font manager, and on macOS
// that picked PingFang SC - a Simplified Chinese face - for Japanese text as
// much as Chinese. Two things went wrong with that:
//
//   - It renders Japanese in Chinese glyph forms. The Han characters the two
//     languages share are not drawn identically (骨, 令, 直 and many others
//     differ), so Japanese titles came out subtly misshapen rather than wrong
//     in a way that announces itself.
//   - PingFang SC Regular is a visibly heavier face than Inter Regular, so a
//     CJK title sitting in a list of Latin ones read as bold, even though
//     nothing had set a weight - the glyph runs resolve at Normal with no font
//     simulations applied. That was the reported symptom.
//
// Naming the fallbacks explicitly fixes both: Japanese picks up Hiragino Sans
// W3 on macOS/iOS, which is both the correct script and a lighter face that
// sits closer to Inter's weight.
//
// One list covers every platform. A family the system does not have is skipped
// rather than treated as an error, so the macOS, Windows and Linux/Android
// names can sit side by side and the first one that actually exists wins.
public static class FlowerFonts
{
    public static AppBuilder WithFlowerFonts(this AppBuilder builder) =>
        builder
            .WithInterFont()
            .With(new FontManagerOptions { FontFallbacks = Fallbacks });

    // Japanese first, deliberately. Japanese and Chinese share the Han block,
    // and nothing at this level knows which language a given string is in - the
    // font manager sees codepoints, not languages - so whichever script leads
    // the list also claims every shared character. There is no ordering that is
    // right for both; this picks the one that is right for the library it is
    // used on (measured: 72 tracks carrying kana, which is unambiguously
    // Japanese, against 31 whose only CJK is Han). Kana and Hangul are
    // unambiguous and resolve to their own fonts regardless of this ordering -
    // it decides Han alone.
    private static readonly FontFallback[] Fallbacks =
    [
        // Japanese. Hiragino Sans is macOS/iOS, Yu Gothic UI/Meiryo are Windows,
        // the Noto names are Linux and Android.
        Fallback("Hiragino Sans"),
        Fallback("Hiragino Kaku Gothic ProN"),
        Fallback("Yu Gothic UI"),
        Fallback("Meiryo"),
        Fallback("Noto Sans CJK JP"),
        Fallback("Noto Sans JP"),

        // Chinese, for the Han characters no Japanese font above happens to
        // cover (simplified-only forms in particular) and for a system with no
        // Japanese font installed at all.
        Fallback("PingFang SC"),
        Fallback("Microsoft YaHei UI"),
        Fallback("Noto Sans CJK SC"),

        // Korean. Hangul is its own block, so this never competes with the
        // entries above - it only matters that something covers it.
        Fallback("Apple SD Gothic Neo"),
        Fallback("Malgun Gothic"),
        Fallback("Noto Sans CJK KR"),
    ];

    private static FontFallback Fallback(string familyName) =>
        new() { FontFamily = new FontFamily(familyName) };
}
