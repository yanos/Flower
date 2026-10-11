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
}
