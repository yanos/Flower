using System;
using System.Collections.Generic;

using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Media;
using Avalonia.Media.TextFormatting;

namespace Flower.Controls;

// One line of text in a list row, laid out once per distinct string and
// shared by every row that shows it.
//
// TextBlock throws its layout away whenever its text changes - and builds two
// for every new string, one to measure and another to arrange - so a list
// that recycles its rows shaped every title, artist and duration that scrolled
// past, again on every pass. Shaping is most of what a row allocates once it
// is no longer being taken off and put back on the tree (UniformRowPanel). Here
// the layouts live in a cache keyed on everything that decides what one looks
// like, so a song scrolled back to costs a lookup, and one duration ("3:42")
// is laid out once for every row that runs that long.
//
// It cannot be a TextBlock subclass that hands TextBlock a cached layout:
// TextBlock disposes its layout on every measure and arrange, and disposing a
// layout returns its glyph buffers to a pool while other rows still draw them.
// So it is a Control that takes the same inherited font properties TextBlock
// does - a style that sets FontWeight on a row reaches it the same way - and
// does only what a row needs: one line, trimmed, aligned.
public sealed class RowText : Control
{
    public static readonly StyledProperty<string?> TextProperty =
        TextBlock.TextProperty.AddOwner<RowText>();

    public static readonly StyledProperty<FontFamily> FontFamilyProperty =
        TextElement.FontFamilyProperty.AddOwner<RowText>();

    public static readonly StyledProperty<double> FontSizeProperty =
        TextElement.FontSizeProperty.AddOwner<RowText>();

    public static readonly StyledProperty<FontWeight> FontWeightProperty =
        TextElement.FontWeightProperty.AddOwner<RowText>();

    public static readonly StyledProperty<FontStyle> FontStyleProperty =
        TextElement.FontStyleProperty.AddOwner<RowText>();

    public static readonly StyledProperty<IBrush?> ForegroundProperty =
        TextElement.ForegroundProperty.AddOwner<RowText>();

    public static readonly StyledProperty<TextAlignment> TextAlignmentProperty =
        TextBlock.TextAlignmentProperty.AddOwner<RowText>();

    public static readonly StyledProperty<TextTrimming> TextTrimmingProperty =
        TextBlock.TextTrimmingProperty.AddOwner<RowText>();

    // A line's height pinned rather than the font's own, where a caption has
    // to be one height whatever script it is in (see AlbumCard).
    public static readonly AttachedProperty<double> LineHeightProperty =
        TextBlock.LineHeightProperty.AddOwner<RowText>();

    static RowText()
    {
        // Foreground too: a layout carries the brush its runs are drawn with,
        // so a new colour is a new layout (see Key).
        AffectsMeasure<RowText>(TextProperty, FontFamilyProperty, FontSizeProperty, FontWeightProperty,
            FontStyleProperty, ForegroundProperty, TextTrimmingProperty, LineHeightProperty);
        AffectsRender<RowText>(TextProperty, FontFamilyProperty, FontSizeProperty, FontWeightProperty,
            FontStyleProperty, ForegroundProperty, TextAlignmentProperty, TextTrimmingProperty, LineHeightProperty);
    }

    public string? Text
    {
        get => GetValue(TextProperty);
        set => SetValue(TextProperty, value);
    }

    public FontFamily FontFamily
    {
        get => GetValue(FontFamilyProperty);
        set => SetValue(FontFamilyProperty, value);
    }

    public double FontSize
    {
        get => GetValue(FontSizeProperty);
        set => SetValue(FontSizeProperty, value);
    }

    public FontWeight FontWeight
    {
        get => GetValue(FontWeightProperty);
        set => SetValue(FontWeightProperty, value);
    }

    public FontStyle FontStyle
    {
        get => GetValue(FontStyleProperty);
        set => SetValue(FontStyleProperty, value);
    }

    public IBrush? Foreground
    {
        get => GetValue(ForegroundProperty);
        set => SetValue(ForegroundProperty, value);
    }

    public TextAlignment TextAlignment
    {
        get => GetValue(TextAlignmentProperty);
        set => SetValue(TextAlignmentProperty, value);
    }

    public TextTrimming TextTrimming
    {
        get => GetValue(TextTrimmingProperty);
        set => SetValue(TextTrimmingProperty, value);
    }

    public double LineHeight
    {
        get => GetValue(LineHeightProperty);
        set => SetValue(LineHeightProperty, value);
    }

    private TextLayout? _layout;

    protected override Size MeasureOverride(Size availableSize)
    {
        _layout = LayoutFor(availableSize.Width);
        return new Size(_layout.WidthIncludingTrailingWhitespace, _layout.Height);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        _layout = LayoutFor(finalSize.Width);
        return finalSize;
    }

    // The line at its own width wherever it fits, which makes it one layout
    // for that string whatever room each row has; a trimmed one, for that
    // width, only where it does not fit. Keyed on the width every time, a
    // song cost a layout per width it was measured and arranged at - six for
    // one row's three lines - and a scroll through a few hundred songs
    // outgrew any cache worth having before it came back to the first.
    private TextLayout LayoutFor(double width)
    {
        var natural = Layouts.Get(this, double.PositiveInfinity);
        if (natural.WidthIncludingTrailingWhitespace <= width || TextTrimming == TextTrimming.None)
            return natural;
        return Layouts.Get(this, width);
    }

    public override void Render(DrawingContext context)
    {
        if (_layout == null)
            return;

        // One line, so aligning it is only where it starts - done here rather
        // than in the layout, which would make it one per width again.
        var spare = Bounds.Width - _layout.WidthIncludingTrailingWhitespace;
        var left = TextAlignment switch
        {
            TextAlignment.Right or TextAlignment.End => spare,
            TextAlignment.Center => spare / 2,
            _ => 0,
        };
        // Centred when squeezed shorter than its line, as TextBlock does.
        var top = Bounds.Height < _layout.Height ? (Bounds.Height - _layout.Height) / 2 : 0;
        _layout.Draw(context, new Point(Math.Max(0, left), top));
    }

    // Everything a layout depends on. Foreground is in it too: a layout
    // carries the brush its runs are drawn with.
    private readonly record struct Key(
        string Text, FontFamily Family, double Size, FontWeight Weight, FontStyle Style,
        IBrush? Foreground, TextTrimming? Trimming, double LineHeight, double MaxWidth);

    // Two generations rather than a true least-recently-used list, so a lookup
    // never allocates: a hit in the older one is moved into the newer, and
    // when the newer fills, the older is dropped whole and they swap. A layout
    // dropped here is left for the collector rather than disposed - a row may
    // still be drawing it.
    private static class Layouts
    {
        private const int Capacity = 2048;
        private static Dictionary<Key, TextLayout> _current = new(Capacity);
        private static Dictionary<Key, TextLayout> _previous = new(Capacity);

        public static TextLayout Get(RowText owner, double maxWidth)
        {
            var key = new Key(owner.Text ?? "", owner.FontFamily, owner.FontSize, owner.FontWeight, owner.FontStyle,
                owner.Foreground, owner.TextTrimming, owner.LineHeight, maxWidth);

            if (_current.TryGetValue(key, out var layout))
                return layout;

            if (!_previous.Remove(key, out layout))
            {
                layout = new TextLayout(key.Text, new Typeface(key.Family, key.Style, key.Weight), key.Size,
                    key.Foreground, TextAlignment.Left, TextWrapping.NoWrap, key.Trimming,
                    maxWidth: maxWidth, lineHeight: key.LineHeight);
            }

            if (_current.Count >= Capacity)
            {
                (_current, _previous) = (_previous, _current);
                _current.Clear();
            }
            _current[key] = layout;
            return layout;
        }
    }
}
