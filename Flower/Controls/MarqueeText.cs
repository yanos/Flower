using System;

using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Media;
using Avalonia.Threading;

using Flower.Services;

namespace Flower.Controls;

// One line of text that, when it is wider than the room it has, slowly scrolls
// round to show the rest instead of ending in an ellipsis - the way Now
// Playing's artist and album lines read on a phone, where a long album name
// otherwise loses exactly the part that tells two editions apart.
//
// A line that fits is centred and still. One that does not sits at its start
// for Pause, travels left at Speed until a second copy, Gap behind the first,
// has taken its place, and starts again - so the loop is seamless rather than
// snapping back. The right edge fades out at rest, which says there is more;
// the left fades too only while the words are moving.
//
// The pause is a one-shot timer and only the travel ticks the shared
// AnimationClock, so a line waiting at its start costs nothing per frame. A
// line that cannot be seen (Now Playing's sheet closed) does not travel: it
// goes back to its start and waits out another pause, which is also how it
// notices being shown again - Avalonia keeps its effective-visibility event
// internal, so a timer every few seconds stands in for one.
public sealed class MarqueeText : Control
{
    public static readonly StyledProperty<string?> TextProperty =
        TextBlock.TextProperty.AddOwner<MarqueeText>();

    public static readonly StyledProperty<double> FontSizeProperty =
        TextElement.FontSizeProperty.AddOwner<MarqueeText>();

    public static readonly StyledProperty<FontFamily> FontFamilyProperty =
        TextElement.FontFamilyProperty.AddOwner<MarqueeText>();

    public static readonly StyledProperty<FontWeight> FontWeightProperty =
        TextElement.FontWeightProperty.AddOwner<MarqueeText>();

    private static readonly TimeSpan Pause = TimeSpan.FromSeconds(3);

    // Pixels per second - slow enough to read while it moves.
    private const double Speed = 30;

    private const double FadeWidth = 16;

    private readonly TextBlock _lead = new();
    private readonly TextBlock _follow = new();
    private readonly TranslateTransform _leadShift = new();
    private readonly TranslateTransform _followShift = new();

    private IDisposable? _pauseTimer;
    private IDisposable? _travel;
    private bool _overflows;
    private bool _isAttached;
    private bool _isMoving;
    private IBrush? _restMask;
    private IBrush? _movingMask;

    static MarqueeText()
    {
        AffectsMeasure<MarqueeText>(TextProperty);
        ClipToBoundsProperty.OverrideDefaultValue<MarqueeText>(true);
    }

    public MarqueeText()
    {
        _lead.RenderTransform = _leadShift;
        _follow.RenderTransform = _followShift;
        _follow.IsVisible = false;
        VisualChildren.Add(_lead);
        VisualChildren.Add(_follow);
        LogicalChildren.Add(_lead);
        LogicalChildren.Add(_follow);
    }

    public string? Text
    {
        get => GetValue(TextProperty);
        set => SetValue(TextProperty, value);
    }

    public double FontSize
    {
        get => GetValue(FontSizeProperty);
        set => SetValue(FontSizeProperty, value);
    }

    public FontFamily FontFamily
    {
        get => GetValue(FontFamilyProperty);
        set => SetValue(FontFamilyProperty, value);
    }

    public FontWeight FontWeight
    {
        get => GetValue(FontWeightProperty);
        set => SetValue(FontWeightProperty, value);
    }

    // Between the end of the words and the copy that follows them, in
    // proportion to the type so it reads as the same pause at any size.
    private double Gap => FontSize * 2.5;

    private double TextWidth => _lead.DesiredSize.Width;

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == TextProperty)
        {
            _lead.Text = Text;
            _follow.Text = Text;
            // A new song starts its line from the beginning, pause included.
            Stop();
            UpdateScrolling();
        }
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        var unbounded = availableSize.WithWidth(double.PositiveInfinity);
        _lead.Measure(unbounded);
        _follow.Measure(unbounded);
        return new Size(Math.Min(TextWidth, availableSize.Width), _lead.DesiredSize.Height);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        var width = TextWidth;
        var height = _lead.DesiredSize.Height;
        var overflows = width > finalSize.Width + 0.5;

        if (overflows)
        {
            _lead.Arrange(new Rect(0, 0, width, height));
            _follow.Arrange(new Rect(width + Gap, 0, width, height));
            _restMask = FadeMask(finalSize.Width, fadeLeft: false);
            _movingMask = FadeMask(finalSize.Width, fadeLeft: true);
        }
        else
        {
            _lead.Arrange(new Rect((finalSize.Width - width) / 2, 0, width, height));
            _restMask = _movingMask = null;
        }

        _follow.IsVisible = overflows;
        OpacityMask = _isMoving ? _movingMask : _restMask;
        if (overflows != _overflows)
        {
            _overflows = overflows;
            Dispatcher.UIThread.Post(UpdateScrolling);
        }
        return finalSize;
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        _isAttached = true;
        UpdateScrolling();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        _isAttached = false;
        Stop();
        base.OnDetachedFromVisualTree(e);
    }

    private bool IsRunning => _pauseTimer != null || _travel != null;

    private void UpdateScrolling()
    {
        var shouldRun = _overflows && _isAttached;
        if (shouldRun && !IsRunning)
            StartCycle();
        else if (!shouldRun && IsRunning)
            Stop();
    }

    private void StartCycle()
    {
        Shift(0);
        _pauseTimer = DispatcherTimer.RunOnce(BeginTravel, Pause);
    }

    private void BeginTravel()
    {
        _pauseTimer = null;
        if (!IsEffectivelyVisible)
        {
            StartCycle();
            return;
        }

        _travel = AnimationClock.Current.Subscribe(elapsed =>
        {
            var offset = elapsed.TotalSeconds * Speed;
            if (offset < TextWidth + Gap && IsEffectivelyVisible)
            {
                Shift(offset);
                return;
            }

            // Either the copy has arrived exactly where the words began, so
            // going back to them is invisible, or nobody is watching - in
            // which case the line is at its start, pause and all, when it is
            // next shown, rather than part-way through words nobody saw begin.
            _travel?.Dispose();
            _travel = null;
            StartCycle();
        });
    }

    private void Stop()
    {
        _pauseTimer?.Dispose();
        _pauseTimer = null;
        _travel?.Dispose();
        _travel = null;
        Shift(0);
    }

    private void Shift(double offset)
    {
        _leadShift.X = -offset;
        _followShift.X = -offset;
        var moving = offset > 0;
        if (moving == _isMoving)
            return;
        _isMoving = moving;
        OpacityMask = moving ? _movingMask : _restMask;
    }

    private static IBrush FadeMask(double width, bool fadeLeft)
    {
        var fade = width > 0 ? Math.Min(FadeWidth / width, 0.25) : 0;
        return new LinearGradientBrush
        {
            StartPoint = new RelativePoint(0, 0.5, RelativeUnit.Relative),
            EndPoint = new RelativePoint(1, 0.5, RelativeUnit.Relative),
            GradientStops =
            {
                new GradientStop(fadeLeft ? Colors.Transparent : Colors.Black, 0),
                new GradientStop(Colors.Black, fadeLeft ? fade : 0),
                new GradientStop(Colors.Black, 1 - fade),
                new GradientStop(Colors.Transparent, 1),
            },
        };
    }
}
