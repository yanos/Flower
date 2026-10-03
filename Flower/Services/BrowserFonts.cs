using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

using Avalonia.Media;
using Avalonia.Media.Fonts;

using Microsoft.Extensions.Logging;

namespace Flower.Services;

// The browser head's fonts beyond Inter.
//
// Every other head borrows its CJK and emoji fonts from the operating system -
// FlowerFonts names Hiragino, Yu Gothic, Noto CJK and so on, and the system
// cascade does the rest. A browser tab cannot: it runs as WebAssembly, draws
// into a canvas with its own Skia, and a web page is not allowed to read the
// machine's installed fonts (that list is a fingerprint). So the tab only has
// the font files it is handed, and until this it was handed Inter and nothing
// else - every Japanese title and every emoji drew as a box, and so did the ♫
// on the album art placeholder.
//
// The fonts that cover those scripts are large (4-8 MB each), and which ones a
// tab needs depends on the library, so they are not bundled. They sit next to
// the app on the server (Flower.Web/wwwroot/fonts) and are fetched by script,
// the first time a catalog contains that script: a library with kana pulls the
// Japanese font, one with Hangul the Korean, one with emoji the emoji font, and
// nothing is downloaded that will never be drawn. They are fetched before the
// catalog is handed to the UI (see App's browser rescan), so rows are first
// laid out with the fonts already there and nothing has to be redrawn.
//
// The one font every tab needs is the one for the app's own chrome: ♫ is not
// in Inter. Noto Sans Symbols is 227 KB and is bundled in Flower.Web itself
// (Assets/Fonts), so it is there from the first frame.
//
// All Noto, under the SIL Open Font License (the licences sit beside the files).
public static class BrowserFonts
{
    // The bundled symbols font's collection, and the location Flower.Web embeds
    // it at.
    private const string SymbolsCollection = "fonts:FlowerSymbols";
    private const string SymbolsSource = "avares://Flower.Web/Assets/Fonts";

    // Family is the name inside the file, which is what the fallback list has
    // to name - a mismatch is a font that loads and is never used, so a test
    // reads each file and holds it to this.
    internal sealed record WebFont(string Family, string File);

    internal static readonly WebFont Emoji = new("Noto Color Emoji", "Noto-COLRv1.ttf");
    internal static readonly WebFont Japanese = new("Noto Sans JP", "NotoSansJP-Regular.otf");
    internal static readonly WebFont SimplifiedChinese = new("Noto Sans SC", "NotoSansSC-Regular.otf");
    internal static readonly WebFont TraditionalChinese = new("Noto Sans TC", "NotoSansTC-Regular.otf");
    internal static readonly WebFont Korean = new("Noto Sans KR", "NotoSansKR-Regular.otf");

    internal static IReadOnlyList<WebFont> Shipped => [Emoji, Japanese, SimplifiedChinese, TraditionalChinese, Korean];

    // Created by Register rather than with this class: a FontCollectionBase
    // asks the platform for its font manager as it is constructed, and the
    // fallback list below is read while the AppBuilder is still being put
    // together, before there is a platform to ask. Created there, it took the
    // tab down at startup.
    private static LoadedFonts? _loaded;
    private static readonly SemaphoreSlim Gate = new(1, 1);
    private static readonly Dictionary<WebFont, GlyphTypeface> Typefaces = new();

    // The fallback list for the browser, in place of FlowerFonts' system one.
    // Order matters only where two of these cover the same character:
    //
    //   - Emoji first. The symbols and CJK fonts carry text-style versions of
    //     59 characters the emoji font draws in colour (☮ ♻ ⚓ ⛩ and the
    //     zodiac among them), and Avalonia picks a fallback by codepoint,
    //     without looking at the U+FE0F that asks for the emoji style - so
    //     whichever comes first wins, and in a song title the emoji is the
    //     likelier meaning.
    //   - Symbols next, for the app's own chrome: the ♫ the emoji font does
    //     not have.
    //
    // No order reaches what Inter itself draws: it is the primary font, asked
    // before any of these, and it has a text-style ❤. So "❤️" draws black
    // here, as it does on the desktop, which has Inter first too. Honouring
    // U+FE0F would take emoji-presentation handling in text layout itself.
    //   - Japanese before Chinese, for the reason FlowerFonts gives: the Han
    //     characters the two share go to whichever comes first, and the
    //     library this was written for is Japanese far more often than not.
    //
    // A family not loaded yet simply does not match, so the list can name all
    // of them from the start.
    public static FontFallback[] Fallbacks =>
    [
        Fallback(Emoji),
        new() { FontFamily = new FontFamily($"{SymbolsCollection}#Noto Sans Symbols") },
        Fallback(Japanese),
        Fallback(SimplifiedChinese),
        Fallback(TraditionalChinese),
        Fallback(Korean),
    ];

    private static FontFallback Fallback(WebFont font) =>
        new() { FontFamily = new FontFamily($"{LoadedFonts.KeyName}#{font.Family}") };

    // Called once at startup, from Flower.Web's AppBuilder.
    public static void Register(FontManager fontManager)
    {
        fontManager.AddFontCollection(new EmbeddedFontCollection(new Uri(SymbolsCollection), new Uri(SymbolsSource)));
        _loaded = new LoadedFonts();
        fontManager.AddFontCollection(_loaded);
    }

    // Fetches whichever fonts these strings need and the tab does not have yet.
    // Never throws: a font that cannot be fetched leaves its characters as
    // boxes, which is what they were before, and is logged.
    public static async Task EnsureForAsync(IEnumerable<string?> texts, HttpClient http, Uri origin, ILogger logger)
    {
        var needs = Scan(texts);
        if (!needs.Any || _loaded is not { } loaded)
            return;

        await Gate.WaitAsync();
        try
        {
            if (needs.Emoji)
                await LoadAsync(loaded, Emoji, http, origin, logger);

            if (needs.Kana || needs.Han.Count > 0 || needs.OtherCjk)
                await LoadAsync(loaded, Japanese, http, origin, logger);

            // Chinese only for Han the Japanese font does not draw: mostly the
            // simplified-only forms, which a Japanese face has no reason to
            // carry. Asked of the font itself rather than guessed from ranges.
            var uncovered = Uncovered(needs.Han, Japanese);
            if (uncovered.Count > 0)
                await LoadAsync(loaded, SimplifiedChinese, http, origin, logger);
            if (Uncovered(uncovered, SimplifiedChinese).Count > 0)
                await LoadAsync(loaded, TraditionalChinese, http, origin, logger);

            if (needs.Hangul)
                await LoadAsync(loaded, Korean, http, origin, logger);
        }
        finally
        {
            Gate.Release();
        }
    }

    private static List<int> Uncovered(IReadOnlyCollection<int> codepoints, WebFont font) =>
        Typefaces.TryGetValue(font, out var typeface)
            ? codepoints.Where(c => !typeface.CharacterToGlyphMap.ContainsGlyph(c)).ToList()
            : codepoints.ToList();

    private static async Task LoadAsync(LoadedFonts loaded, WebFont font, HttpClient http, Uri origin, ILogger logger)
    {
        if (Typefaces.ContainsKey(font))
            return;

        try
        {
            var bytes = await http.GetByteArrayAsync(new Uri(origin, "fonts/" + font.File));

            // Not disposed: the typeface may read from it for as long as the
            // tab is open.
            var stream = new MemoryStream(bytes, writable: false);
            if (loaded.TryAddGlyphTypeface(stream, out var typeface))
            {
                Typefaces[font] = typeface;
                logger.LogInformation("Loaded {Family} ({Kilobytes} KB) for this library's text", font.Family, bytes.Length / 1024);
            }
            else
            {
                logger.LogWarning("{File} did not load as a font; its characters will show as boxes", font.File);
            }
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            logger.LogWarning(ex, "Could not fetch {File}; its characters will show as boxes", font.File);
        }
    }

    // What a set of strings needs, by script. Internal for the tests.
    internal sealed class Needs
    {
        public bool Emoji;
        public bool Kana;
        public bool Hangul;
        // CJK punctuation and fullwidth forms, which Inter does not carry and
        // a Japanese font does.
        public bool OtherCjk;
        public readonly HashSet<int> Han = [];

        public bool Any => Emoji || Kana || Hangul || OtherCjk || Han.Count > 0;
    }

    internal static Needs Scan(IEnumerable<string?> texts)
    {
        var needs = new Needs();
        foreach (var text in texts)
        {
            if (string.IsNullOrEmpty(text))
                continue;

            for (var i = 0; i < text.Length; i++)
            {
                var c = text[i];
                // Everything Inter draws sits below here - Latin, Greek,
                // Cyrillic, general punctuation - so most characters of most
                // libraries cost one comparison.
                if (c < '←')
                    continue;

                var codepoint = char.IsHighSurrogate(c) && i + 1 < text.Length && char.IsLowSurrogate(text[i + 1])
                    ? char.ConvertToUtf32(c, text[++i])
                    : c;
                Classify(codepoint, needs);
            }
        }

        return needs;
    }

    private static void Classify(int c, Needs needs)
    {
        if (IsHan(c))
            needs.Han.Add(c);
        else if (c is >= 0x3040 and <= 0x30FF or >= 0x31F0 and <= 0x31FF or >= 0xFF66 and <= 0xFF9F)
            needs.Kana = true;
        else if (c is >= 0xAC00 and <= 0xD7AF or >= 0x1100 and <= 0x11FF or >= 0x3130 and <= 0x318F or >= 0xA960 and <= 0xA97F or >= 0xD7B0 and <= 0xD7FF)
            needs.Hangul = true;
        else if (IsEmoji(c))
            needs.Emoji = true;
        else if (c is >= 0x3000 and <= 0x303F or >= 0xFF00 and <= 0xFFEF)
            needs.OtherCjk = true;
    }

    private static bool IsHan(int c) =>
        c is >= 0x4E00 and <= 0x9FFF or >= 0x3400 and <= 0x4DBF or >= 0xF900 and <= 0xFAFF or >= 0x20000 and <= 0x3134F;

    // Emoji by block rather than by the full Extended_Pictographic property:
    // the blocks below are where a song title's emoji live, and the property
    // would also claim symbols Inter or the symbols font already draw in the
    // text style a title would want. U+FE0F, the emoji-style selector, asks
    // for the emoji font whatever precedes it.
    private static bool IsEmoji(int c) =>
        c is >= 0x1F000 and <= 0x1FAFF
            or >= 0x2600 and <= 0x27BF
            or >= 0x2B00 and <= 0x2BFF
            or 0xFE0F;

    private sealed class LoadedFonts : FontCollectionBase
    {
        public const string KeyName = "fonts:FlowerWeb";

        public override Uri Key { get; } = new(KeyName);
    }
}
