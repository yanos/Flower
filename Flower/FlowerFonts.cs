using System;

using Avalonia;
using Avalonia.Media;
using Avalonia.Media.Fonts;

namespace Flower;

// The app-wide font setup, shared by all four entry points (Desktop, Android,
// iOS, Web) so the fallback list cannot drift between platforms.
//
// Geist is the UI font (Theme.axaml's AppDisplayFontFamily is the serif for
// titles), bundled in Assets/Fonts in the four weights the app uses. It has
// almost no Greek and none of CJK, so Inter - still referenced for this - is
// the first fallback: what Inter drew before Geist, it still draws, and on the
// browser, which has no system fonts to fall back to, a Greek title is Inter
// rather than boxes (BrowserFonts.Scan counts on that).
//
// Anything outside both is resolved by the platform font manager, and on macOS
// that picked PingFang SC - a Simplified Chinese face - for Japanese text as
// much as Chinese. Two things went wrong with that:
//
//   - It renders Japanese in Chinese glyph forms. The Han characters the two
//     languages share are not drawn identically (骨, 令, 直 and many others
//     differ), so Japanese titles came out subtly misshapen rather than wrong
//     in a way that announces itself.
//   - PingFang SC Regular is a visibly heavier face than the UI font, so a
//     CJK title sitting in a list of Latin ones read as bold, even though
//     nothing had set a weight - the glyph runs resolve at Normal with no font
//     simulations applied. That was the reported symptom.
//
// Naming the fallbacks explicitly fixes both: Japanese picks up Hiragino Sans
// W3 on macOS/iOS, which is both the correct script and a lighter face that
// sits closer to the UI font's weight.
//
// One list covers every platform. A family the system does not have is skipped
// rather than treated as an error, so the macOS, Windows and Linux/Android
// names can sit side by side and the first one that actually exists wins.
public static class FlowerFonts
{
    // What a TextBlock outside any templated control is drawn in; inside
    // one, Fluent's ContentControlThemeFontFamily says the same (Theme.axaml).
    public const string UiFamily = "avares://Flower/Assets/Fonts#Geist";

    // Inter's own collection, which WithInterFont registers.
    internal static readonly FontFallback Inter = new() { FontFamily = new FontFamily("fonts:Inter#Inter") };

    // The browser has no system fonts to fall back to, so it gets its own list
    // and the fonts behind it - see BrowserFonts.
    public static AppBuilder WithFlowerFonts(this AppBuilder builder) =>
        OperatingSystem.IsBrowser()
            ? builder
                .WithInterFont()
                .ConfigureFonts(Services.BrowserFonts.Register)
                .With(new FontManagerOptions { DefaultFamilyName = UiFamily, FontFallbacks = Services.BrowserFonts.Fallbacks })
            : builder
                .WithInterFont()
                .With(new FontManagerOptions { DefaultFamilyName = UiFamily, FontFallbacks = Fallbacks });

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
        Inter,

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
