using System;
using System.IO;
using System.Linq;

using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Media;
using Avalonia.Media.Fonts;
using Avalonia.Media.Imaging;

using Flower.Services;

namespace Flower.Tests;

// The browser head's fonts (BrowserFonts): which scripts a library's text asks
// for, and whether the files shipped for them are the fonts the fallback list
// thinks they are.
public class BrowserFontsTests
{
    [Fact]
    public void Latin_text_asks_for_nothing()
    {
        Assert.False(BrowserFonts.Scan(["Abbey Road", "Björk", "Сплин", "Ελληνικά", "Café – live"]).Any);
    }

    [Fact]
    public void Kana_asks_for_Japanese_and_Hangul_for_Korean()
    {
        var needs = BrowserFonts.Scan(["ヨルシカ - だから僕は音楽を辞めた", "아이유"]);

        Assert.True(needs.Kana);
        Assert.True(needs.Hangul);
        Assert.False(needs.Emoji);
    }

    [Fact]
    public void Han_is_collected_by_codepoint_so_its_coverage_can_be_checked()
    {
        var needs = BrowserFonts.Scan(["花譜", "花"]);

        Assert.Equal([0x82B1, 0x8B5C], needs.Han.Order());
    }

    // Including one outside the BMP, which arrives as a surrogate pair and has
    // to be read as one character.
    [Theory]
    [InlineData("🎵 Music")]
    [InlineData("☀ Sunshine")]
    [InlineData("Love ❤️")]
    public void An_emoji_asks_for_the_emoji_font(string text)
    {
        Assert.True(BrowserFonts.Scan([text]).Emoji);
    }

    [Fact]
    public void Null_and_empty_text_ask_for_nothing()
    {
        Assert.False(BrowserFonts.Scan([null, ""]).Any);
    }

    // The files Flower.Web serves, read the way the tab reads them. A family
    // name that differs from BrowserFonts' is a font that downloads and is
    // never drawn with, which nothing at runtime would report.
    [AvaloniaFact]
    public void Each_shipped_font_loads_under_the_family_the_fallback_list_names()
    {
        foreach (var font in BrowserFonts.Shipped)
        {
            var typeface = Load(font);
            Assert.Equal(font.Family, typeface.FamilyName);
        }
    }

    [AvaloniaFact]
    public void Each_shipped_font_draws_the_script_it_is_fetched_for()
    {
        Assert.True(Load(BrowserFonts.Japanese).CharacterToGlyphMap.ContainsGlyph('の'));
        Assert.True(Load(BrowserFonts.Japanese).CharacterToGlyphMap.ContainsGlyph('譜'));
        Assert.True(Load(BrowserFonts.SimplifiedChinese).CharacterToGlyphMap.ContainsGlyph('这'));
        Assert.True(Load(BrowserFonts.TraditionalChinese).CharacterToGlyphMap.ContainsGlyph('體'));
        Assert.True(Load(BrowserFonts.Korean).CharacterToGlyphMap.ContainsGlyph('한'));
        Assert.True(Load(BrowserFonts.Emoji).CharacterToGlyphMap.ContainsGlyph(0x1F3B5));
    }

    // The point of the Chinese fallback: a simplified-only form the Japanese
    // font has no reason to carry, which is how EnsureForAsync decides to
    // fetch the Chinese one at all.
    [AvaloniaFact]
    public void The_Japanese_font_lacks_simplified_only_forms()
    {
        Assert.False(Load(BrowserFonts.Japanese).CharacterToGlyphMap.ContainsGlyph('这'));
    }

    private static int _collections;

    private static GlyphTypeface Load(BrowserFonts.WebFont font)
    {
        var collection = new TestFonts(new Uri($"fonts:BrowserFontsTests{System.Threading.Interlocked.Increment(ref _collections)}"));
        using var stream = File.OpenRead(Path.Combine(FontsDirectory, font.File));
        var copy = new MemoryStream();
        stream.CopyTo(copy);
        copy.Position = 0;

        Assert.True(collection.TryAddGlyphTypeface(copy, out var typeface), $"{font.File} did not load");
        return typeface;
    }

    private static string FontsDirectory
    {
        get
        {
            for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir != null; dir = dir.Parent)
            {
                var candidate = Path.Combine(dir.FullName, "Flower.Web", "wwwroot", "fonts");
                if (Directory.Exists(candidate))
                    return candidate;
            }

            throw new DirectoryNotFoundException("Flower.Web/wwwroot/fonts is not above the test assembly");
        }
    }

    private sealed class TestFonts(Uri key) : FontCollectionBase
    {
        public override Uri Key { get; } = key;
    }
}
