using System;
using System.Collections.Generic;
using System.Linq;

using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless.XUnit;
using Avalonia.Themes.Fluent;
using Avalonia.Threading;
using Avalonia.VisualTree;

using Flower.Models;
using Flower.Tests.TestSupport;
using Flower.ViewModels.Mobile;
using Flower.Views.Mobile.Screens;

using Microsoft.Extensions.Logging.Abstractions;

using Xunit;

using UniformGrid = Avalonia.Controls.Primitives.UniformGrid;
using Track = Flower.Models.Track;

namespace Flower.Tests;

// Two album tiles fill a phone in portrait but leave the art absurdly large
// once the phone is turned sideways, so the grids derive their column count
// from their own measured width (AlbumGridColumnSizing -> AlbumGridRow.
// ColumnsFor -> MobileMainViewModel.AlbumGridColumns, which re-chunks the rows
// already built). AlbumGridRowTests covers that arithmetic on its own; these
// go through the real view, because the parts of the chain that can silently
// do nothing are the ones only a laid-out control exercises - whether the
// attached property ever sees a width, and whether the row template's
// UniformGrid.Columns binding, which lives inside an ItemsPanelTemplate,
// resolves at all.
[Collection("PlatformDataDirectory")]
public class AlbumGridColumnSizingTests : PinnedDataDirectory
{
    private static Track TrackIn(string album) => new()
    {
        Title = album + " track",
        Album = album,
        Artists = "Someone",
        Path = "/music/" + album + ".flac",
        DateAdded = DateTimeOffset.UtcNow,
    };

    private static MainViewModelHarness.MobileParts BuildWithAlbums(int count)
    {
        var tracks = Enumerable.Range(0, count).Select(i => TrackIn("Album " + i)).ToList();
        var parts = MainViewModelHarness.BuildParts(new Library(tracks), new MainPlaylist(new List<Track>()));
        var mobile = new MobileMainViewModel(parts.Main, parts.PlaylistControl, parts.CurrentlyPlaying,
            NullLogger<MobileMainViewModel>.Instance);
        Dispatcher.UIThread.RunJobs();
        return new MainViewModelHarness.MobileParts(mobile, parts);
    }

    private static Window ShowGrid(MobileMainViewModel mobile, double width)
    {
        // TestAppBuilder runs a bare Application with no theme, so without this
        // ItemsControl has no default template at all - it measures, realizes
        // nothing, and every assertion below reads an empty visual tree. Added
        // to the window rather than the Application because the suite shares
        // one Application across every test (PerAssembly isolation).
        var window = new Window { Width = width, Height = 700 };
        window.Styles.Add(new FluentTheme());
        window.Content = new AlbumGridScreenView { DataContext = mobile };
        window.Show();
        Layout(window, width);
        return window;
    }

    private static void Layout(Window window, double width)
    {
        window.Width = width;
        // Two passes: the attached property reacts to the width the first one
        // hands it, and the re-chunk that follows only reaches the tree on the
        // next.
        for (var i = 0; i < 2; i++)
        {
            window.Measure(new Size(width, 700));
            window.Arrange(new Rect(0, 0, width, 700));
            window.UpdateLayout();
            Dispatcher.UIThread.RunJobs();
        }
    }

    private static int RenderedColumns(Window window) =>
        window.GetVisualDescendants().OfType<UniformGrid>().First().Columns;

    [AvaloniaFact]
    public void A_portrait_width_lays_the_grid_out_two_across()
    {
        using var scope = BuildWithAlbums(12);
        scope.Mobile.SelectTabCommand.Execute(nameof(MobileTab.Albums));
        var window = ShowGrid(scope.Mobile, 390);

        Assert.Equal(2, scope.Mobile.AlbumGridColumns);
        Assert.Equal(2, RenderedColumns(window));
        window.Close();
    }

    [AvaloniaFact]
    public void A_landscape_width_fits_more_albums_across()
    {
        using var scope = BuildWithAlbums(12);
        scope.Mobile.SelectTabCommand.Execute(nameof(MobileTab.Albums));
        var window = ShowGrid(scope.Mobile, 844);

        Assert.Equal(5, scope.Mobile.AlbumGridColumns);
        Assert.Equal(5, RenderedColumns(window));
        window.Close();
    }

    // Rotating the device has to reflow what is already on screen, not wait
    // for the next library update to re-chunk it.
    [AvaloniaFact]
    public void Rotating_reflows_the_rows_already_built()
    {
        using var scope = BuildWithAlbums(12);
        scope.Mobile.SelectTabCommand.Execute(nameof(MobileTab.Albums));
        var window = ShowGrid(scope.Mobile, 390);
        Assert.Equal(6, scope.Mobile.AlbumGridRows.Count);

        Layout(window, 844);

        Assert.Equal(3, scope.Mobile.AlbumGridRows.Count);
        Assert.Equal(5, RenderedColumns(window));
        window.Close();
    }

    // Every tile keeps the same width whichever row it lands in, including the
    // two stragglers on the short last row of a five-column grid.
    [AvaloniaFact]
    public void A_short_trailing_row_sizes_its_tiles_like_a_full_one()
    {
        using var scope = BuildWithAlbums(12);
        scope.Mobile.SelectTabCommand.Execute(nameof(MobileTab.Albums));
        var window = ShowGrid(scope.Mobile, 844);

        var widths = window.GetVisualDescendants().OfType<Button>()
            .Where(b => b.Classes.Contains("pickerRow"))
            .Select(b => Math.Round(b.Bounds.Width))
            .Distinct()
            .ToList();

        Assert.True(widths[0] > 0, "tiles were never laid out");
        Assert.Single(widths);
        window.Close();
    }
}
