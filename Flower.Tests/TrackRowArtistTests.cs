using System;
using System.Collections.Generic;
using System.Linq;

using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Themes.Fluent;
using Avalonia.Threading;
using Avalonia.VisualTree;

using Flower.Models;
using Flower.Tests.TestSupport;
using Flower.ViewModels;
using Flower.ViewModels.Mobile;
using Flower.Views.Mobile.Screens;

using Microsoft.Extensions.Logging.Abstractions;

using Xunit;

using Track = Flower.Models.Track;

namespace Flower.Tests;

// A track row prints the artist under the title, which inside one album meant
// the same name repeated down the whole screen. The hosting screen decides
// (ITrackRowHost.ShowsRowArtist) rather than the row, because "the same for
// this whole view" is a property of the list, not of a track.
[Collection("PlatformDataDirectory")]
public class TrackRowArtistTests : PinnedDataDirectory
{
    public TrackRowArtistTests() => TestIoc.EnsureConfigured();

    private const string Album = "Red Medicine";

    private static Track TrackBy(string title, string artist) => new()
    {
        Title = title,
        Album = Album,
        Artists = artist,
        Path = "/music/" + title + ".flac",
        DateAdded = DateTimeOffset.UtcNow,
    };

    private static MainViewModelHarness.MobileParts BuildInAlbum(params string[] artists)
    {
        var tracks = artists.Select((a, i) => TrackBy("Song " + i, a)).ToList();
        var parts = MainViewModelHarness.BuildParts(new Library(tracks), new MainPlaylist(new List<Track>()));
        var mobile = new MobileMainViewModel(parts.Main, parts.PlaylistControl, parts.CurrentlyPlaying,
            NullLogger<MobileMainViewModel>.Instance);
        mobile.SelectTabCommand.Execute(nameof(MobileTab.Albums));
        mobile.SelectAlbumOrArtistCommand.Execute(Album);
        Dispatcher.UIThread.RunJobs();
        return new MainViewModelHarness.MobileParts(mobile, parts);
    }

    private static (Window Window, TrackListScreenView View) Show(MobileMainViewModel mobile)
    {
        // No theme comes from TestAppBuilder, so without FluentTheme nothing
        // has a control template and the visual tree comes back empty.
        var view = new TrackListScreenView { DataContext = mobile };
        var window = new Window { Width = 390, Height = 700 };
        window.Styles.Add(new FluentTheme());
        window.Content = view;
        window.Show();
        view.ObserveLive(mobile);
        window.Measure(new Size(390, 700));
        window.Arrange(new Rect(0, 0, 390, 700));
        window.UpdateLayout();
        Dispatcher.UIThread.RunJobs();
        return (window, view);
    }

    // Only the rows - the album header names the artist too, and that one is
    // the whole point: it says it once instead of once per song.
    private static List<string?> ArtistLinesOnRows(Window window) =>
        window.GetVisualDescendants().OfType<Button>()
            .Where(b => b.Classes.Contains("trackRow"))
            .SelectMany(b => b.GetVisualDescendants().OfType<TextBlock>())
            .Where(t => t.IsVisible)
            .Select(t => t.Text)
            .ToList();

    [AvaloniaFact]
    public void One_artist_across_the_list_drops_the_line_from_every_row()
    {
        using var scope = BuildInAlbum("Fugazi", "Fugazi", "Fugazi");
        var (window, view) = Show(scope.Mobile);

        Assert.False(view.ShowsRowArtist);
        Assert.DoesNotContain("Fugazi", ArtistLinesOnRows(window));
        Assert.Contains("Song 0", ArtistLinesOnRows(window));

        window.Close();
    }

    [AvaloniaFact]
    public void A_compilation_keeps_the_artist_on_each_row()
    {
        using var scope = BuildInAlbum("Fugazi", "Slint", "Unwound");
        var (window, view) = Show(scope.Mobile);

        Assert.True(view.ShowsRowArtist);
        var lines = ArtistLinesOnRows(window);
        Assert.Contains("Fugazi", lines);
        Assert.Contains("Slint", lines);
        Assert.Contains("Unwound", lines);

        window.Close();
    }

    private static double RowHeight(Window window) =>
        window.GetVisualDescendants().OfType<Button>()
            .First(b => b.Classes.Contains("trackRow")).Bounds.Height;

    // Hiding the line is only half of it: the trailing "..." button is taller
    // than a single line of title on its own, so without trimming that too the
    // row measured to exactly the same height it had with the artist on it.
    [AvaloniaFact]
    public void Dropping_the_artist_gives_the_height_back()
    {
        using var oneArtist = BuildInAlbum("Fugazi", "Fugazi", "Fugazi");
        var (tight, _) = Show(oneArtist.Mobile);
        var tightHeight = RowHeight(tight);
        tight.Close();

        using var mixed = BuildInAlbum("Fugazi", "Slint", "Unwound");
        var (tall, _) = Show(mixed.Mobile);
        var tallHeight = RowHeight(tall);
        tall.Close();

        Assert.True(tightHeight < tallHeight - 4,
            $"a row with no artist ({tightHeight}) is barely shorter than one with ({tallHeight})");
    }

    // One row is not a repetition, and in a list with no album header above it
    // that line is the only place the artist is named at all.
    [AvaloniaFact]
    public void A_single_row_keeps_its_artist()
    {
        using var scope = BuildInAlbum("Fugazi");
        var (window, view) = Show(scope.Mobile);

        Assert.True(view.ShowsRowArtist);
        Assert.Contains("Fugazi", ArtistLinesOnRows(window));

        window.Close();
    }
}
