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

namespace Flower.Tests.Browser;

// The files Flower.Web serves, read from Flower.Web/wwwroot/fonts in the
// source tree. A class of their own so Flower.Tests.iOS can leave them out:
// the simulator app carries the test assembly and not the repository around
// it, and these are about a browser asset rather than the platform the suite
// runs on.
public class BrowserFontFilesTests
{
    // Read the way the tab reads them. A family name that differs from
    // BrowserFonts' is a font that downloads and is never drawn with, which
    // nothing at runtime would report.
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
        var collection = new TestFonts(new Uri($"fonts:BrowserFontFilesTests{System.Threading.Interlocked.Increment(ref _collections)}"));
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
