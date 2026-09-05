using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;

using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Themes.Fluent;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Avalonia.Controls.Primitives;
using Avalonia.Styling;

using Flower.Models;
using Flower.Tests.TestSupport;
using Flower.ViewModels;
using Flower.ViewModels.Mobile;
using Flower.Controls;
using Flower.Views.Mobile.Screens;

using Microsoft.Extensions.Logging.Abstractions;

using Xunit;

using Track = Flower.Models.Track;

namespace Flower.Tests;

// Stacked above the tracks, an album's 240px art ate most of a landscape
// screen before a single song was visible, so past a width threshold the
// header pins to the left of a scrolling track list instead
// (TrackListScreenView.IsWideAlbumLayout). The two layouts are one Grid whose
// first column collapses when narrow rather than two alternative subtrees, and
// the track list is deliberately built only once across both - a hidden
// ItemsControl still generates containers, which is the whole reason
// DisplayRows exists. These assert both of those, laid out for real.
[Collection("PlatformDataDirectory")]
public class AlbumDetailLayoutTests : PinnedDataDirectory
{
    public AlbumDetailLayoutTests() => TestIoc.EnsureConfigured();

    private const string Album = "Noble and Godlike in Ruin";

    private static Track TrackIn(string title) => new()
    {
        Title = title,
        Album = Album,
        Artists = "Deerhoof",
        Path = "/music/" + title + ".flac",
        DateAdded = DateTimeOffset.UtcNow,
    };

    private static MainViewModelHarness.MobileParts BuildInAlbum()
    {
        var tracks = Enumerable.Range(0, 8).Select(i => TrackIn("Song " + i)).ToList();
        var parts = MainViewModelHarness.BuildParts(new Library(tracks), new MainPlaylist(new List<Track>()));
        var mobile = new MobileMainViewModel(parts.Main, parts.PlaylistControl, parts.CurrentlyPlaying,
            NullLogger<MobileMainViewModel>.Instance);
        mobile.SelectTabCommand.Execute(nameof(MobileTab.Albums));
        mobile.SelectAlbumOrArtistCommand.Execute(Album);
        MainViewModelHarness.WaitForTheDrillIn(mobile, Album);
        return new MainViewModelHarness.MobileParts(mobile, parts);
    }

    private static (Window Window, TrackListScreenView View) Show(
        MobileMainViewModel mobile, double width, bool withRowPadding = false)
    {
        // TestAppBuilder runs a bare Application with no theme, so without this
        // nothing here has a control template and the visual tree comes back
        // empty. On the window rather than the Application because the suite
        // shares one Application across every test (PerAssembly isolation).
        var view = new TrackListScreenView { DataContext = mobile };
        var window = new Window { Width = width, Height = 700 };
        window.Styles.Add(new FluentTheme());
        if (withRowPadding)
            window.Styles.Add(TrackRowPadding());
        window.Content = view;
        window.Show();
        view.ObserveLive(mobile);
        Layout(window, width);
        return (window, view);
    }

    // The app's own mobile styles are declared inline in MobileMainView.axaml,
    // so there is nothing to StyleInclude here - a track row falls back to
    // FluentTheme's 8px Button padding instead of the 16 it has on a device,
    // and the alignment test would be measuring the wrong inset. This restates
    // just that one setter. It has to stay in step with
    // MobileMainView.axaml's "Button.pickerRow, Button.trackRow" style, which
    // is the number the album text's own margin is chosen to match.
    private static Style TrackRowPadding()
    {
        var style = new Style(x => x.OfType<Button>().Class("trackRow"));
        style.Setters.Add(new Setter(TemplatedControl.PaddingProperty, new Thickness(16, 5)));
        style.Setters.Add(new Setter(TemplatedControl.BorderThicknessProperty, new Thickness(0)));
        return style;
    }

    private static void Layout(Window window, double width)
    {
        window.Width = width;
        // Twice: the first pass is what tells the view how wide it is, and the
        // layout that choice implies only lands on the next one.
        for (var i = 0; i < 2; i++)
        {
            window.Measure(new Size(width, 700));
            window.Arrange(new Rect(0, 0, width, 700));
            window.UpdateLayout();
            Dispatcher.UIThread.RunJobs();
        }
    }

    // Bounds are relative to a control's own parent, and these two live in
    // different branches of the Grid - so every position here is compared in
    // window space.
    private static Rect InWindow(Window window, Visual control) =>
        new(control.TranslatePoint(default, window) ?? default, control.Bounds.Size);

    // The header ContentControls are the only ones bound to DisplayHeader;
    // the art ones are the ones that ended up with an art view under them.
    private static List<ContentControl> Headers(Window window) =>
        window.GetVisualDescendants().OfType<ContentControl>()
            .Where(c => c.ContentTemplate != null && c.Content is AlbumTileViewModel)
            .ToList();

    private static ContentControl VisibleArt(Window window) =>
        Assert.Single(Headers(window).Where(h =>
            h.IsVisible && h.GetVisualDescendants().OfType<SquareAlbumArtView>().Any()));

    private static ContentControl AlbumText(Window window) =>
        Assert.Single(Headers(window).Where(h =>
            h.IsVisible && !h.GetVisualDescendants().OfType<SquareAlbumArtView>().Any()));

    [AvaloniaFact]
    public void A_narrow_screen_stacks_the_header_above_the_tracks()
    {
        using var scope = BuildInAlbum();
        var (window, view) = Show(scope.Mobile, 390);

        Assert.False(view.IsWideAlbumLayout);

        // Both inside the scroller, so they sit above the first track rather
        // than beside it - and the tracks get the full width.
        Assert.NotNull(VisibleArt(window).FindAncestorOfType<ScrollViewer>());
        Assert.NotNull(AlbumText(window).FindAncestorOfType<ScrollViewer>());
        Assert.True(InWindow(window, VisibleArt(window)).Bottom
            <= InWindow(window, AlbumText(window)).Y, "the words are not under the art");
        Assert.Equal(0, Math.Round(InWindow(window, TrackList(window)).X));

        window.Close();
    }

    [AvaloniaFact]
    public void A_wide_screen_pins_the_art_left_of_the_tracks()
    {
        using var scope = BuildInAlbum();
        var (window, view) = Show(scope.Mobile, 844);

        Assert.True(view.IsWideAlbumLayout);

        // The art alone is hoisted out of the scroll; the words stay in it, at
        // the top of the track column, so they scroll away with the songs.
        var artControl = VisibleArt(window);
        Assert.Null(artControl.FindAncestorOfType<ScrollViewer>());
        Assert.NotNull(AlbumText(window).FindAncestorOfType<ScrollViewer>());

        var art = InWindow(window, artControl);
        var text = InWindow(window, AlbumText(window));
        var tracks = InWindow(window, TrackList(window));
        Assert.True(art.Right <= tracks.X, $"art {art} overlaps tracks {tracks}");
        Assert.True(art.Right <= text.X, $"art {art} overlaps the words {text}");
        Assert.True(text.Y < tracks.Y, "the words are not above the songs");
        Assert.True(tracks.Width > 0, "tracks got no width");

        window.Close();
    }

    // "Use the max amount of vertical space": the pinned art is square and
    // sized off the height it was given, not a fixed width - a Grid will not
    // do that on its own, since the Auto column measures with unconstrained
    // width (see PinnedArtSize).
    [AvaloniaFact]
    public void The_pinned_art_fills_the_height_it_is_given()
    {
        using var scope = BuildInAlbum();
        var (window, view) = Show(scope.Mobile, 2000);

        // 700 tall minus the 16px margin top and bottom, and well inside the
        // 40% of 2000 the width cap would allow.
        Assert.Equal(668, Math.Round(view.PinnedArtSize));

        var art = Assert.Single(window.GetVisualDescendants().OfType<SquareAlbumArtView>()
            .Where(a => a.IsVisible && a.FindAncestorOfType<ScrollViewer>() == null));
        Assert.Equal(668, Math.Round(art.Bounds.Width));
        Assert.Equal(668, Math.Round(art.Bounds.Height));

        window.Close();
    }

    // ...but not past 40% of the width, or a tall window would leave the songs
    // a column an inch wide.
    [AvaloniaFact]
    public void The_pinned_art_never_takes_more_than_its_share_of_the_width()
    {
        using var scope = BuildInAlbum();
        var (window, view) = Show(scope.Mobile, 700);

        Assert.Equal(280, Math.Round(view.PinnedArtSize));
        Assert.True(InWindow(window, TrackList(window)).Width > 350, "the songs lost their column");

        window.Close();
    }

    // The pinned column is Auto and empty when narrow, so it has to measure to
    // nothing rather than reserving 200px the tracks never get back.
    [AvaloniaFact]
    public void Rotating_hands_the_pinned_column_width_back_to_the_tracks()
    {
        using var scope = BuildInAlbum();
        var (window, _) = Show(scope.Mobile, 844);
        var beside = TrackList(window).Bounds.Width;

        Layout(window, 390);

        Assert.Equal(390, Math.Round(TrackList(window).Bounds.Width));
        Assert.True(beside < 844, "the wide layout never made room for the header");

        window.Close();
    }

    // Two alternative subtrees would each build their own copy - and a hidden
    // ItemsControl still generates containers, so the copy that is not showing
    // would silently realize every row of it.
    [AvaloniaFact]
    public void Only_one_track_list_is_ever_built()
    {
        using var scope = BuildInAlbum();
        var (window, _) = Show(scope.Mobile, 844);

        var lists = window.GetVisualDescendants().OfType<ItemsControl>()
            .Where(c => c is not ListBox && ReferenceEquals(c.ItemsSource, scope.Mobile.AlbumDetailRows))
            .ToList();

        Assert.Single(lists);

        window.Close();
    }

    // Ranged left at the head of the track column, the album name has to start
    // on exactly the same vertical as the song titles under it - which means
    // matching Button.trackRow's own horizontal padding, not the art's margin.
    [AvaloniaFact]
    public void The_album_name_lines_up_with_the_song_titles()
    {
        using var scope = BuildInAlbum();
        var (window, _) = Show(scope.Mobile, 844, withRowPadding: true);

        var name = TextBlockSaying(window, Album);
        var song = TextBlockSaying(window, "Song 0");
        Assert.Equal(Math.Round(InWindow(window, song).X), Math.Round(InWindow(window, name).X));

        window.Close();
    }

    private static TextBlock TextBlockSaying(Window window, string text) =>
        window.GetVisualDescendants().OfType<TextBlock>().First(t => t.Text == text);

    private static ItemsControl TrackList(Window window) =>
        window.GetVisualDescendants().OfType<ItemsControl>()
            .First(c => c is not ListBox && c.ItemsSource is IReadOnlyList<TrackRowViewModel>);
}
