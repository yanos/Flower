using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Media;

using Xunit;

namespace Flower.Tests;

/// <summary>
/// The mobile art grids (AlbumGridScreenView and friends) lay two album tiles
/// out per row, so the two captions have to occupy the same height or the row
/// stops lining up. They do not do that for free: Inter carries no CJK glyphs,
/// so a Japanese title resolves through a system fallback face whose metrics
/// are 1.45x to 1.6x taller, and an unpinned caption then measures 39px next to
/// a Latin one's 28px. The grids pin both lines with explicit LineHeights to
/// take the font out of it - these tests hold that contract, and the literal
/// sizes here must stay in step with the XAML.
///
/// Which fallback face, and so which of those ratios, is a fact about the
/// machine rather than about Flower: the same Japanese title measures 19px on
/// one Mac and 21px on a GitHub runner. The pins were first set from the
/// smaller of those with a pixel to spare, which is how the third test below
/// failed on CI having passed everywhere it was written. They now clear the
/// taller one, at a cost of two pixels of caption on every device.
/// </summary>
public sealed class AlbumTileCaptionTests
{
    private const double TitleLineHeight = 21;
    private const double ArtistLineHeight = 18;

    private static StackPanel Caption(string name, string artist)
    {
        var caption = new StackPanel();
        caption.Children.Add(new TextBlock
        {
            Text = name,
            FontSize = 13,
            LineHeight = TitleLineHeight,
            FontWeight = FontWeight.Medium,
            TextTrimming = TextTrimming.CharacterEllipsis,
        });
        caption.Children.Add(new TextBlock
        {
            Text = artist,
            FontSize = 11,
            LineHeight = ArtistLineHeight,
            Opacity = 0.6,
            TextTrimming = TextTrimming.CharacterEllipsis,
        });
        caption.Measure(new Size(170, double.PositiveInfinity));
        return caption;
    }

    [AvaloniaFact]
    public void A_cjk_caption_is_the_same_height_as_a_latin_one()
    {
        var latin = Caption("Noble and Godlike in Ruin", "Deerhoof");
        var japanese = Caption("星間性交", "telepathテレパシー能力者");

        Assert.Equal(latin.DesiredSize.Height, japanese.DesiredSize.Height);
    }

    [AvaloniaFact]
    public void A_caption_is_a_fixed_height_whatever_it_says()
    {
        const double expected = TitleLineHeight + ArtistLineHeight;

        Assert.Equal(expected, Caption("Noble and Godlike in Ruin", "Deerhoof").DesiredSize.Height);
        Assert.Equal(expected, Caption("星間性交", "telepathテレパシー能力者").DesiredSize.Height);
    }

    /// <summary>
    /// The pinned heights only work if they leave the tallest script room to
    /// sit in; a LineHeight under what the fallback face actually needs would
    /// squeeze the glyphs rather than align them.
    /// </summary>
    [AvaloniaFact]
    public void The_pinned_heights_clear_what_cjk_glyphs_need_unpinned()
    {
        var title = new TextBlock { Text = "星間性交", FontSize = 13 };
        var artist = new TextBlock { Text = "telepathテレパシー能力者", FontSize = 11 };
        title.Measure(new Size(170, double.PositiveInfinity));
        artist.Measure(new Size(170, double.PositiveInfinity));

        // Both numbers in one message, rather than an Assert.True per line
        // that stops at the first: what these are worth on a machine other than
        // this one is the whole question, and a failure that reports half of it
        // costs a round-trip through CI to learn the other half.
        Assert.True(
            title.DesiredSize.Height <= TitleLineHeight && artist.DesiredSize.Height <= ArtistLineHeight,
            $"a pinned caption squeezes this machine's CJK fallback: title needs "
            + $"{title.DesiredSize.Height} of {TitleLineHeight}, artist needs "
            + $"{artist.DesiredSize.Height} of {ArtistLineHeight}");
    }
}
