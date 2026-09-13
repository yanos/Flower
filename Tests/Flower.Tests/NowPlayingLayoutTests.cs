using System.Collections.Generic;
using System.Linq;

using Avalonia;
using Avalonia.Controls;
using Thumb = Avalonia.Controls.Primitives.Thumb;
using Avalonia.Headless.XUnit;
using Avalonia.Themes.Fluent;
using Avalonia.Threading;
using Avalonia.VisualTree;

using Flower.Models;
using Flower.Tests.TestSupport;
using Flower.ViewModels.Mobile;
using Flower.Views.Mobile;

using Microsoft.Extensions.Logging.Abstractions;

using Xunit;

namespace Flower.Tests;

// The now-playing sheet has to hold two shapes: upright, where the cover sits
// above the controls, and turned sideways, where the sheet is only a couple of
// hundred pixels tall and a portrait-sized cover overflows its row and paints
// down over the seek slider - which is the bug these are about. Geometry only:
// the art is square, it never overlaps the block below or beside it, and it is
// worth looking at in both.
public class NowPlayingLayoutTests
{
    private sealed class Laid
    {
        public required Window Window { get; init; }
        public required Rect Art { get; init; }
        public required Rect Controls { get; init; }
    }

    private static Laid LayOut(double width, double height)
    {
        var parts = MainViewModelHarness.BuildParts(new Library(new List<Track>()), new MainPlaylist(new List<Track>()));
        using var scope = parts;
        var mobile = new MobileMainViewModel(parts.Main, parts.PlaylistControl, parts.CurrentlyPlaying,
            NullLogger<MobileMainViewModel>.Instance);
        Dispatcher.UIThread.RunJobs();

        // TestAppBuilder runs a bare Application with no theme, so the Slider and
        // Buttons in here would otherwise have no template to measure at all.
        var window = new Window { Width = width, Height = height };
        window.Styles.Add(new FluentTheme());
        window.Content = new NowPlayingView { DataContext = mobile };
        window.Show();
        window.Measure(new Size(width, height));
        window.Arrange(new Rect(0, 0, width, height));
        window.UpdateLayout();
        Dispatcher.UIThread.RunJobs();

        var body = window.GetVisualDescendants().OfType<Flower.Controls.NowPlayingBodyPanel>().Single();
        var art = body.Children[0];
        var controls = body.Children[1];
        return new Laid
        {
            Window = window,
            Art = art.Bounds,
            Controls = controls.Bounds,
        };
    }

    [AvaloniaFact]
    public void Upright_puts_the_cover_above_the_controls()
    {
        var laid = LayOut(390, 844);

        Assert.Equal(laid.Art.Width, laid.Art.Height, 1);
        Assert.True(laid.Art.Bottom <= laid.Controls.Top, $"art {laid.Art} overlaps controls {laid.Controls}");
        Assert.True(laid.Art.Width > 200, $"cover only {laid.Art.Width} wide upright");
        laid.Window.Close();
    }

    [AvaloniaFact]
    public void Turned_sideways_puts_the_cover_beside_them_instead()
    {
        var laid = LayOut(844, 390);

        Assert.Equal(laid.Art.Width, laid.Art.Height, 1);
        Assert.True(laid.Art.Right <= laid.Controls.Left, $"art {laid.Art} overlaps controls {laid.Controls}");

        // The whole point of the reflow: the cover is sized by the short edge,
        // so it is most of the sheet's height rather than a thumbnail.
        Assert.True(laid.Art.Height > laid.Controls.Height / 2, $"cover only {laid.Art.Height} tall sideways");
        laid.Window.Close();
    }

    // Fluent's Slider template puts its 20px track 15px down a 50px-tall
    // control, so a Height that closes that gap crops the template rather than
    // shrinking it and the bottom of the thumb is cut off - which is what both
    // sliders in here looked like. The invariant is the one the eye checks:
    // the whole thumb is inside the control that owns it.
    [AvaloniaTheory]
    [InlineData(390, 844)]
    [InlineData(844, 390)]
    public void Both_sliders_draw_their_whole_thumb(double width, double height)
    {
        var laid = LayOut(width, height);

        var sliders = laid.Window.GetVisualDescendants().OfType<Slider>().ToList();
        Assert.Equal(2, sliders.Count);

        foreach (var slider in sliders)
        {
            var thumb = slider.GetVisualDescendants().OfType<Thumb>().Single();
            var top = thumb.TranslatePoint(new Point(0, 0), slider);
            Assert.NotNull(top);

            Assert.True(top.Value.Y >= -0.5 && top.Value.Y + thumb.Bounds.Height <= slider.Bounds.Height + 0.5,
                $"thumb at y={top.Value.Y} h={thumb.Bounds.Height} escapes its {slider.Bounds.Height}-tall slider");
        }

        laid.Window.Close();
    }
}
