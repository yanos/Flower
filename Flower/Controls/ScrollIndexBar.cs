using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.VisualTree;

using Flower.Services;

namespace Flower.Controls;

/// <summary>
/// The thin strip down the right edge of mobile's long lists that jumps the list
/// to a place in it: A-Z on the alphabetical ones (Songs, Albums, Artists), and
/// a column of dots on Recently Added, which is in date order and so has no
/// letter to go to - there the dots are a scrubber over the whole scroll. The
/// Albums and Artists lists are dots too while sorted by anything else.
/// Touching it jumps, and dragging along it keeps jumping.
/// </summary>
/// <remarks>
/// <para>
/// The bar owns the gesture and the scrolling; the one thing it cannot know is
/// where a letter is in a given list, which is <see cref="IndexOfLetter"/>, set
/// by the screen. Letters mode is <see cref="IndexOfLetter"/> being set and
/// <see cref="ShowsLetters"/> true; otherwise it is dots.
/// </para>
/// <para>
/// A list with nothing to scroll gets no bar at all: it measures to zero width,
/// which also gives back the column a list beside it would otherwise lose.
/// </para>
/// <para>
/// The bar is laid out beside or over the list rather than inside its scroller,
/// so it does not scroll, and a press on it never reaches the scroller's own
/// gesture recognizer. ScreenStackPanel's swipe-back sees it on the way down
/// all the same (it tunnels), and is told to leave it alone - a drag down the
/// bar that wanders sideways is still a drag down the bar.
/// </para>
/// </remarks>
public class ScrollIndexBar : Control
{
    public static readonly StyledProperty<ItemsControl?> TargetProperty =
        AvaloniaProperty.Register<ScrollIndexBar, ItemsControl?>(nameof(Target));

    public static readonly StyledProperty<bool> ShowsLettersProperty =
        AvaloniaProperty.Register<ScrollIndexBar, bool>(nameof(ShowsLetters), true);

    public static readonly StyledProperty<bool> LettersDescendingProperty =
        AvaloniaProperty.Register<ScrollIndexBar, bool>(nameof(LettersDescending));

    public static readonly StyledProperty<IBrush?> ForegroundProperty =
        AvaloniaProperty.Register<ScrollIndexBar, IBrush?>(nameof(Foreground), Brushes.Gray);

    public static readonly StyledProperty<IBrush?> ActiveBackgroundProperty =
        AvaloniaProperty.Register<ScrollIndexBar, IBrush?>(nameof(ActiveBackground));

    public static readonly StyledProperty<IBrush?> BubbleBackgroundProperty =
        AvaloniaProperty.Register<ScrollIndexBar, IBrush?>(nameof(BubbleBackground), Brushes.DimGray);

    public static readonly StyledProperty<IBrush?> BubbleForegroundProperty =
        AvaloniaProperty.Register<ScrollIndexBar, IBrush?>(nameof(BubbleForeground), Brushes.White);

    // Wide enough for a thumb to find without looking, narrow enough to leave
    // the list its width. The letters are drawn in the middle of it.
    public const double BarWidth = 20;

    // The pitch between letters when there is room for it, and the least
    // they can be squeezed to before the bar starts leaving letters out
    // (drawing a dot in place of every other one, the way iOS does).
    private const double PreferredSlotHeight = 16;
    private const double MinSlotHeight = 12;
    private const double FontSize = 11;

    // The letter under the finger, drawn large to the left of it, since the
    // finger itself is covering the one on the bar.
    private const double BubbleSize = 52;
    private const double BubbleGap = 28;

    private ScrollViewer? _scroller;
    private bool _hasRoom;
    private bool _isScrubbing;
    private double _scrubY;
    private char? _currentLetter;
    private double _currentFraction = double.NaN;

    static ScrollIndexBar()
    {
        AffectsRender<ScrollIndexBar>(ShowsLettersProperty, LettersDescendingProperty, ForegroundProperty, ActiveBackgroundProperty,
            BubbleBackgroundProperty, BubbleForegroundProperty);
    }

    public ScrollIndexBar()
    {
        ClipToBounds = false;
        HorizontalAlignment = HorizontalAlignment.Right;
    }

    /// <summary>
    /// The list the bar moves: a ListBox, whose own scroller it uses, or an
    /// ItemsControl stacked inside a ScrollViewer, which it scrolls instead.
    /// </summary>
    public ItemsControl? Target
    {
        get => GetValue(TargetProperty);
        set => SetValue(TargetProperty, value);
    }

    /// <summary>
    /// Whether the list is in an order <see cref="IndexOfLetter"/> can find a
    /// letter in. False turns the bar to dots while it stays false - the same
    /// list sorted by year, say - without the screen giving up its lookup.
    /// </summary>
    public bool ShowsLetters
    {
        get => GetValue(ShowsLettersProperty);
        set => SetValue(ShowsLettersProperty, value);
    }

    /// <summary>
    /// Whether the list runs Z to A, so the bar does too: Z at the top and #
    /// at the bottom, where a reversed sort puts them.
    /// </summary>
    public bool LettersDescending
    {
        get => GetValue(LettersDescendingProperty);
        set => SetValue(LettersDescendingProperty, value);
    }

    private IReadOnlyList<char> Letters =>
        LettersDescending ? AlphabetIndex.LettersDescending : AlphabetIndex.Letters;

    public IBrush? Foreground
    {
        get => GetValue(ForegroundProperty);
        set => SetValue(ForegroundProperty, value);
    }

    public IBrush? ActiveBackground
    {
        get => GetValue(ActiveBackgroundProperty);
        set => SetValue(ActiveBackgroundProperty, value);
    }

    public IBrush? BubbleBackground
    {
        get => GetValue(BubbleBackgroundProperty);
        set => SetValue(BubbleBackgroundProperty, value);
    }

    public IBrush? BubbleForeground
    {
        get => GetValue(BubbleForegroundProperty);
        set => SetValue(BubbleForegroundProperty, value);
    }

    /// <summary>
    /// Where in <see cref="Target"/>'s items a letter's first entry is, or -1
    /// for nowhere. Null makes this a bar of dots.
    /// </summary>
    public Func<char, int>? IndexOfLetter { get; set; }

    private bool IsDotted => IndexOfLetter == null || !ShowsLetters;

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property != TargetProperty)
            return;

        // A ListBox that has never been shown has no template yet, so no
        // scroller to find until it has one.
        if (change.OldValue is TemplatedControl oldTarget)
            oldTarget.TemplateApplied -= OnTargetTemplateApplied;
        if (change.NewValue is TemplatedControl newTarget)
            newTarget.TemplateApplied += OnTargetTemplateApplied;
        if (IsLoaded)
            AttachScroller();
    }

    private void OnTargetTemplateApplied(object? sender, TemplateAppliedEventArgs e)
    {
        if (IsLoaded)
            AttachScroller();
    }

    protected override void OnLoaded(RoutedEventArgs e)
    {
        base.OnLoaded(e);
        AttachScroller();
    }

    protected override void OnUnloaded(RoutedEventArgs e)
    {
        base.OnUnloaded(e);
        DetachScroller();
    }

    private void AttachScroller()
    {
        DetachScroller();
        _scroller = Target switch
        {
            null => null,
            // A ListBox scrolls itself; anything else is inside the scroller.
            ListBox listBox => listBox.GetVisualDescendants().OfType<ScrollViewer>().FirstOrDefault(),
            var items => items.FindAncestorOfType<ScrollViewer>(),
        };
        if (_scroller != null)
            _scroller.ScrollChanged += OnScrollChanged;
        UpdateHasRoom();
    }

    private void DetachScroller()
    {
        if (_scroller != null)
            _scroller.ScrollChanged -= OnScrollChanged;
        _scroller = null;
    }

    // Fires on extent and viewport changes as well as on scrolling, which is
    // what brings the bar in when a list grows long enough to need it.
    private void OnScrollChanged(object? sender, ScrollChangedEventArgs e) => UpdateHasRoom();

    private void UpdateHasRoom()
    {
        // A screen's worth of slack, not zero: the lists all keep room past
        // their end for the chrome floating over it, so a list that fits on
        // screen still scrolls a little, and a bar over that is noise.
        var hasRoom = _scroller is { } s && s.Extent.Height - s.Viewport.Height > s.Viewport.Height * 0.5;
        if (hasRoom == _hasRoom)
            return;
        _hasRoom = hasRoom;
        if (!hasRoom)
            EndScrub();
        InvalidateMeasure();
        InvalidateVisual();
    }

    protected override Size MeasureOverride(Size availableSize) =>
        new(_hasRoom ? BarWidth : 0, 0);

    // The slots the bar's height is divided into and where they start: letters
    // (or dots) at a comfortable pitch, centred in the height the bar was
    // given, and squeezed - down to MinSlotHeight - before any are dropped.
    private (int Slots, double SlotHeight, double Top) Layout()
    {
        var height = Bounds.Height;
        var letters = Letters.Count;
        var fit = (int)Math.Floor(height / MinSlotHeight);
        var slots = Math.Max(1, Math.Min(letters, fit));

        // Squeezed, every other slot is a dot standing for the letters skipped,
        // and the last slot must be a letter: an odd count keeps the last letter on the bar.
        if (slots < letters && slots % 2 == 0)
            slots--;

        var slotHeight = Math.Min(PreferredSlotHeight, height / slots);
        var top = (height - slots * slotHeight) / 2;
        return (slots, slotHeight, top);
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        if (!_hasRoom || _scroller == null)
            return;

        e.Pointer.Capture(this);
        _isScrubbing = true;
        _currentLetter = null;
        _currentFraction = double.NaN;
        e.Handled = true;
        Scrub(e.GetPosition(this).Y);
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        if (!_isScrubbing)
            return;
        e.Handled = true;
        Scrub(e.GetPosition(this).Y);
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        if (!_isScrubbing)
            return;
        e.Handled = true;
        e.Pointer.Capture(null);
        EndScrub();
    }

    protected override void OnPointerCaptureLost(PointerCaptureLostEventArgs e)
    {
        base.OnPointerCaptureLost(e);
        EndScrub();
    }

    private void EndScrub()
    {
        if (!_isScrubbing)
            return;
        _isScrubbing = false;
        InvalidateVisual();
    }

    /// <summary>
    /// Where along the bar's slots <paramref name="y"/> is, 0 at the top of the
    /// first and 1 at the bottom of the last - clamped, so a finger that runs
    /// off either end holds the first or the last letter.
    /// </summary>
    private double FractionAt(double y)
    {
        var (slots, slotHeight, top) = Layout();
        return Math.Clamp((y - top) / (slots * slotHeight), 0, 1);
    }

    /// <summary>
    /// Where along the bar a finger picks <paramref name="letter"/> - the
    /// middle of its share of the bar, whether or not it is drawn there.
    /// </summary>
    internal double YOf(char letter)
    {
        var (slots, slotHeight, top) = Layout();
        var letters = Letters;
        var index = Math.Max(0, IndexOf(letters, letter));
        return top + (index + 0.5) * slots * slotHeight / letters.Count;
    }

    /// <summary>The top and bottom of the run of letters or dots.</summary>
    internal (double Top, double Bottom) Span
    {
        get
        {
            var (slots, slotHeight, top) = Layout();
            return (top, top + slots * slotHeight);
        }
    }

    private static int IndexOf(IReadOnlyList<char> letters, char letter)
    {
        for (var i = 0; i < letters.Count; i++)
        {
            if (letters[i] == letter)
                return i;
        }
        return -1;
    }

    private void Scrub(double y)
    {
        _scrubY = y;
        var fraction = FractionAt(y);
        if (!IsDotted && IndexOfLetter is { } indexOf)
        {
            // Every letter can be reached even when the bar is too short to
            // draw them all: the finger maps onto the whole alphabet, not onto
            // what is drawn.
            var letters = Letters;
            var letter = letters[Math.Min(letters.Count - 1, (int)(fraction * letters.Count))];
            if (letter != _currentLetter)
            {
                _currentLetter = letter;
                var index = indexOf(letter);
                if (index >= 0)
                    BringToTop(index);
            }
        }
        else if (fraction != _currentFraction)
        {
            _currentFraction = fraction;
            ScrollToFraction(fraction);
        }

        InvalidateVisual();
    }

    private void ScrollToFraction(double fraction)
    {
        if (_scroller is not { } scroller)
            return;
        var max = Math.Max(0, scroller.Extent.Height - scroller.Viewport.Height);
        scroller.Offset = new Vector(scroller.Offset.X, fraction * max);
    }

    /// <summary>
    /// Scrolls item <paramref name="index"/> of <see cref="Target"/> up to the
    /// top of the visible list - level with the bar's own top, which sits
    /// below the header band the lists run under.
    /// </summary>
    private void BringToTop(int index)
    {
        if (Target is not { } items || _scroller is not { } scroller)
            return;

        // Realized first, so it has a position to line up: a jump across a
        // long list lands on rows that do not exist yet.
        items.ScrollIntoView(index);
        scroller.UpdateLayout();
        if (items.ContainerFromIndex(index) is not { } container)
            return;

        var itemTop = container.TranslatePoint(default, scroller)?.Y;
        var visibleTop = this.TranslatePoint(default, scroller)?.Y;
        if (itemTop is not { } y || visibleTop is not { } top)
            return;

        var max = Math.Max(0, scroller.Extent.Height - scroller.Viewport.Height);
        var offset = Math.Clamp(scroller.Offset.Y + y - top, 0, max);
        scroller.Offset = new Vector(scroller.Offset.X, offset);
    }

    public override void Render(DrawingContext context)
    {
        if (!_hasRoom)
            return;

        var (slots, slotHeight, top) = Layout();
        var width = Bounds.Width;

        // Hit-testing follows what is drawn, so the gaps between letters
        // would let a press through to the list underneath without this.
        context.DrawRectangle(Brushes.Transparent, null, new Rect(Bounds.Size));

        if (_isScrubbing && ActiveBackground is { } active)
        {
            var pill = new Rect(0, top - 4, width, slots * slotHeight + 8);
            context.DrawRectangle(active, null, pill, width / 2, width / 2);
        }

        var letters = Letters;
        var squeezed = slots < letters.Count;
        for (var i = 0; i < slots; i++)
        {
            var centreY = top + (i + 0.5) * slotHeight;
            if (IsDotted || (squeezed && i % 2 == 1))
            {
                DrawDot(context, new Point(width / 2, centreY));
                continue;
            }

            // Spread across the whole alphabet, so squeezed the first and last
            // slots are still the first and last letters and the ones between are evenly spaced.
            var letter = squeezed
                ? letters[(int)Math.Round(i * (letters.Count - 1) / (double)(slots - 1))]
                : letters[i];
            DrawCentred(context, letter.ToString(), FontSize, FontWeight.SemiBold, Foreground,
                new Point(width / 2, centreY));
        }

        if (_isScrubbing && !IsDotted && _currentLetter is { } current)
            DrawBubble(context, current, _scrubY);
    }

    private void DrawDot(DrawingContext context, Point centre)
    {
        if (Foreground is { } brush)
            context.DrawEllipse(brush, null, centre, 2, 2);
    }

    private void DrawBubble(DrawingContext context, char letter, double y)
    {
        var centreY = Math.Clamp(y, BubbleSize / 2, Math.Max(BubbleSize / 2, Bounds.Height - BubbleSize / 2));
        var centre = new Point(-BubbleGap - BubbleSize / 2, centreY);
        if (BubbleBackground is { } background)
            context.DrawEllipse(background, null, centre, BubbleSize / 2, BubbleSize / 2);
        DrawCentred(context, letter.ToString(), 26, FontWeight.SemiBold, BubbleForeground, centre);
    }

    private void DrawCentred(DrawingContext context, string text, double size, FontWeight weight, IBrush? brush, Point centre)
    {
        var typeface = new Typeface(GetValue(TextElement.FontFamilyProperty), FontStyle.Normal, weight);
        var formatted = new FormattedText(text, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight,
            typeface, size, brush);
        context.DrawText(formatted, new Point(centre.X - formatted.Width / 2, centre.Y - formatted.Height / 2));
    }
}
