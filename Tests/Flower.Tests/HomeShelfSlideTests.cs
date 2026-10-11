using System;
using System.Collections.Generic;
using System.Linq;

using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;

using Flower.Controls;
using Flower.Models;
using Flower.Services;
using Flower.Tests.TestSupport;
using Flower.ViewModels;
using Flower.Views;

using Xunit;

namespace Flower.Tests;

// A cover that moves when Home rebuilds slides from where it was to where it
// is now, rather than jumping there - see HomeShelfRowsHost.
[Collection("PlatformDataDirectory")]
public class HomeShelfSlideTests : PinnedDataDirectory
{
    private static List<Track> Album(string name) =>
        Enumerable.Range(1, 2).Select(i => new Track
        {
            Title = $"{name} {i}",
            Path = $"/music/{name}/{i}.flac",
            Album = name,
            Artists = $"{name} Band",
            TrackNumber = (uint)i,
            Duration = TimeSpan.FromMinutes(4),
        }).ToList();

    private static void Start(AlbumProgressTracker progress, List<Track> album)
    {
        progress.TrackStarted(album[0], shuffling: false);
        progress.TrackStarted(album[1], shuffling: false);
    }

    // Its Continue Playing cover: what its row (TileRowPanel) holds, which
    // is what the slide moves. (It is on Recently Added as well.)
    private static Control CoverOf(Window window, string album)
    {
        var card = window.GetVisualDescendants().OfType<AlbumCard>().Single(c => c.ShowsProgress && c.Tile?.Name == album);
        return card.GetVisualAncestors().OfType<Control>().First(c => c.GetVisualParent() is TileRowPanel);
    }

    private static double OffsetOf(Control cover) => cover.RenderTransform is TranslateTransform t ? t.X : 0;

    [AvaloniaFact]
    public void An_album_moving_to_the_front_slides_there_and_pushes_the_other_along()
    {
        var red = Album("Red");
        var blue = Album("Blue");
        var library = new Library(red.Concat(blue).ToList());
        var progress = new AlbumProgressTracker(library);
        using var home = new HomeViewModel(library, progress, null);
        Start(progress, blue);
        Start(progress, red);
        home.Rebuild();

        var window = new Window { Width = 1200, Height = 800 };
        window.Styles.Add(new Avalonia.Themes.Fluent.FluentTheme());
        var view = new HomeView { DataContext = home };
        var now = TimeSpan.Zero;
        var clock = new AnimationClock(() => now);
        view.ShelfRows.ClockOverride = clock;
        window.Content = view;
        window.Show();
        Dispatcher.UIThread.RunJobs();
        window.UpdateLayout();

        Start(progress, blue);
        home.Rebuild();
        window.UpdateLayout();

        // Each drawn back where it was - the two swapped, so one cell to the
        // right of where layout put Blue and one to the left of Red - before
        // either is first drawn at its new place.
        Assert.Equal(new[] { "Blue", "Red" }, home.ContinuePlaying.Select(c => c.Tile.Name));
        var cell = CoverOf(window, "Red").Bounds.X - CoverOf(window, "Blue").Bounds.X;
        Assert.True(cell > 100, $"cell {cell}");
        Assert.Equal(cell, OffsetOf(CoverOf(window, "Blue")), 1);
        Assert.Equal(-cell, OffsetOf(CoverOf(window, "Red")), 1);

        now += TimeSpan.FromMilliseconds(100);
        clock.TickForTest();
        Assert.InRange(OffsetOf(CoverOf(window, "Blue")), 1, cell - 1);

        now += TimeSpan.FromMilliseconds(400);
        clock.TickForTest();
        Assert.Null(CoverOf(window, "Blue").RenderTransform);
        Assert.Null(CoverOf(window, "Red").RenderTransform);
        window.Close();
    }
}
