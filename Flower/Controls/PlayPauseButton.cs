using System;

using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Media;

namespace Flower.Controls;

// The round Play/Pause button: a disc in the button's Background with
// Material's Play or Pause glyph cut out of it, so whatever is under the
// button - the cover's colour on Now Playing, the mini player's oval - shows
// through the glyph rather than a second colour painted over the fill. Now
// Playing's transport and the mini player both use it.
//
// The disc is drawn here rather than by the template's presenter, whose fill
// cannot have a hole in it: Button.playPause (Styles/IconButtons.axaml), a
// class every one of these carries, clears the presenter's Background, and a
// host restyles the disc through Background like any button's fill. It keeps
// Button's own theme and styles (StyleKeyOverride), so it takes taps, press
// states and a Command as every other button does.
public sealed class PlayPauseButton : Button
{
    public static readonly StyledProperty<bool> IsPlayingProperty =
        AvaloniaProperty.Register<PlayPauseButton, bool>(nameof(IsPlaying));

    /// <summary>
    /// The glyph's box, in pixels, which Material draws in 24 units: the size
    /// the MaterialIcon it replaces was given.
    /// </summary>
    public static readonly StyledProperty<double> GlyphSizeProperty =
        AvaloniaProperty.Register<PlayPauseButton, double>(nameof(GlyphSize), 24);

    // Material's Pause and Play (Material.Icons' path data for each), in its
    // 24-unit box: two bars, and a triangle.
    private static readonly Point[][] PauseGlyph =
    [
        [new(6, 5), new(10, 5), new(10, 19), new(6, 19)],
        [new(14, 5), new(18, 5), new(18, 19), new(14, 19)],
    ];

    private static readonly Point[][] PlayGlyph =
    [
        [new(8, 5.14), new(19, 12.14), new(8, 19.14)],
    ];

    static PlayPauseButton()
    {
        AffectsRender<PlayPauseButton>(BackgroundProperty, IsPlayingProperty, GlyphSizeProperty);
    }

    public PlayPauseButton()
    {
        Classes.Add("playPause");
        UpdateAutomationName();
    }

    protected override Type StyleKeyOverride => typeof(Button);

    public bool IsPlaying
    {
        get => GetValue(IsPlayingProperty);
        set => SetValue(IsPlayingProperty, value);
    }

    public double GlyphSize
    {
        get => GetValue(GlyphSizeProperty);
        set => SetValue(GlyphSizeProperty, value);
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == IsPlayingProperty)
            UpdateAutomationName();
    }

    // Drawn under the template, whose presenter is clear (Button.playPause),
    // so the disc is what shows.
    public override void Render(DrawingContext context)
    {
        base.Render(context);
        if (Background is not { } fill)
            return;

        var radius = Math.Min(Bounds.Width, Bounds.Height) / 2;
        if (radius <= 0)
            return;

        var centre = new Point(Bounds.Width / 2, Bounds.Height / 2);
        var scale = GlyphSize / 24;
        var origin = new Point(centre.X - GlyphSize / 2, centre.Y - GlyphSize / 2);

        var geometry = new StreamGeometry();
        using (var ctx = geometry.Open())
        {
            // Even-odd, so the glyph inside the disc is a hole in it.
            ctx.SetFillRule(FillRule.EvenOdd);

            ctx.BeginFigure(new Point(centre.X - radius, centre.Y), isFilled: true);
            ctx.ArcTo(new Point(centre.X + radius, centre.Y), new Size(radius, radius), 0, false, SweepDirection.Clockwise);
            ctx.ArcTo(new Point(centre.X - radius, centre.Y), new Size(radius, radius), 0, false, SweepDirection.Clockwise);
            ctx.EndFigure(isClosed: true);

            foreach (var figure in IsPlaying ? PauseGlyph : PlayGlyph)
            {
                ctx.BeginFigure(origin + figure[0] * scale, isFilled: true);
                for (var i = 1; i < figure.Length; i++)
                    ctx.LineTo(origin + figure[i] * scale);
                ctx.EndFigure(isClosed: true);
            }
        }

        context.DrawGeometry(fill, null, geometry);
    }

    // Screen readers hear what a tap will do, as the glyph shows it.
    private void UpdateAutomationName() =>
        AutomationProperties.SetName(this, IsPlaying ? "Pause" : "Play");
}
