using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;

using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Markup.Xaml.Styling;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.Themes.Fluent;
using Avalonia.Threading;
using Avalonia.VisualTree;

using Flower.Controls;
using Flower.Models;
using Flower.Tests.TestSupport;
using Flower.ViewModels;
using Flower.ViewModels.Mobile;
using Flower.Views.Mobile;
using Flower.Views.Mobile.Screens;

using Material.Icons;
using Material.Icons.Avalonia;

using Microsoft.Extensions.Logging.Abstractions;

using Xunit;

using Track = Flower.Models.Track;

namespace Flower.Tests;

// A playlist's own tracks are a screen about one thing, the way an album's
// are, and used to be the only such screen with nothing at the top of it
// saying so. It shows the album screen's header now - the same markup
// (AlbumHeaderTemplates.axaml), so its cover, its name and its pill of
// actions can only ever be changed for both - with the count of its songs
// where an artist's name goes and, until a playlist can carry a picture of
// its own, an empty cover.
[Collection("PlatformDataDirectory")]
public class PlaylistDetailLayoutTests : PinnedDataDirectory
{
    public PlaylistDetailLayoutTests() => TestIoc.EnsureConfigured();

    private const string PlaylistName = "Songs to hum badly";

    private static Track SomeTrack(string title) => new()
    {
        Title = title,
        Album = "Bee Thousand",
        Artists = "Guided by Voices",
        Path = "/music/" + title + ".flac",
        DateAdded = DateTimeOffset.UtcNow,
    };

    // The whole MobileMainView rather than a bare TrackListScreenView: a
    // playlist's header goes into the screenScroll ListBox template, which is
    // declared there (see ScreenScroll.Header), and so is every style the
    // header's own pill of actions needs.
    private sealed class Harness : IDisposable
    {
        public Window Window { get; }
        public MobileMainViewModel Vm => _parts.Mobile;

        private readonly MainViewModelHarness.MobileParts _parts;

        public Harness(int songs)
        {
            var tracks = Enumerable.Range(0, songs).Select(i => SomeTrack("Song " + i)).ToList();
            var library = new Library(tracks);
            library.AddPlaylist(new Playlist(PlaylistName, tracks));

            _parts = MainViewModelHarness.BuildMobile(library, new MainPlaylist(new List<Track>()));
            Window = new Window { Width = 390, Height = 800 };
            Window.Styles.Add(new FluentTheme());
            Window.Content = new MobileMainView { DataContext = Vm };
            Window.Show();
            Pump();

            Vm.SelectTabCommand.Execute(nameof(MobileTab.Playlists));
            Pump(500);
            Vm.SelectPlaylistCommand.Execute(Vm.PlaylistPickerItems.First(i => i.Name == PlaylistName));
            // Past the entrance easing, so the screen is at rest and the rows
            // the drill-in rebuilt on the pool have landed.
            Pump(700);

            Assert.True(Vm.IsShowingPlaylistTracks, "never drilled into the playlist");
        }

        public static void Pump(int milliseconds = 150)
        {
            using var cts = new CancellationTokenSource(milliseconds);
            Dispatcher.UIThread.MainLoop(cts.Token);
        }

        public void Dispose()
        {
            Window.Close();
            _parts.Dispose();
        }
    }

    // Searched inside the track list screen rather than the whole window: the
    // screen behind it is kept alive for the swipe back, and it is the
    // playlist picker - which has a row saying this playlist's name too.
    private static TrackListScreenView Screen(Window window) =>
        window.GetVisualDescendants().OfType<TrackListScreenView>().Last(v => v.IsEffectivelyVisible);

    private static TextBlock TextBlockSaying(Window window, string text) =>
        Screen(window).GetVisualDescendants().OfType<TextBlock>().First(t => t.Text == text && t.IsEffectivelyVisible);

    private static Rect InWindow(Window window, Visual control) =>
        new(control.TranslatePoint(default, window) ?? default, control.Bounds.Size);

    [AvaloniaFact]
    public void The_playlist_says_its_name_and_how_many_songs_it_holds()
    {
        using var harness = new Harness(3);
        var window = harness.Window;

        var name = TextBlockSaying(window, PlaylistName);
        var count = TextBlockSaying(window, "3 songs");
        var firstSong = TextBlockSaying(window, "Song 0");

        Assert.True(InWindow(window, name).Bottom <= InWindow(window, count).Y, "the count is not under the name");
        Assert.True(InWindow(window, count).Bottom <= InWindow(window, firstSong).Y, "the header is not above the songs");

    }

    // One song is not "1 songs".
    [AvaloniaFact]
    public void One_song_is_counted_in_the_singular()
    {
        using var harness = new Harness(1);
        Assert.Equal("1 song", harness.Vm.CurrentPlaylistHeader?.Artist);
    }

    // The point of the exercise: the same buttons the album screen has, from
    // the same markup, acting on the same rows - except adding to a playlist,
    // which a playlist's songs already are in. A pencil takes its place.
    [AvaloniaFact]
    public void It_offers_play_shuffle_and_a_pencil_but_not_add_to_playlist()
    {
        using var harness = new Harness(3);
        var window = harness.Window;

        var kinds = Screen(window).GetVisualDescendants().OfType<MaterialIcon>()
            .Where(i => i.IsEffectivelyVisible && i.FindAncestorOfType<Button>() is { Classes: var c } && c.Contains("pill"))
            .Select(i => i.Kind)
            .ToList();

        Assert.Contains(MaterialIconKind.Play, kinds);
        Assert.Contains(MaterialIconKind.Shuffle, kinds);
        Assert.Contains(MaterialIconKind.Pencil, kinds);
        Assert.DoesNotContain(MaterialIconKind.PlaylistPlus, kinds);

    }

    // On an ordinary playlist the pencil turns the name in the header into a
    // box, focused, and what is typed there is the playlist's new name once
    // Enter ends it - the rename the row's own menu offers, from its screen.
    [AvaloniaFact]
    public void The_pencil_renames_the_playlist_in_its_header()
    {
        using var harness = new Harness(3);
        var window = harness.Window;

        harness.Vm.EditCurrentPlaylistCommand.Execute(null);
        Harness.Pump();

        var editor = Assert.Single(Screen(window).GetVisualDescendants().OfType<PlaylistNameEditor>(), e => e.IsEffectivelyVisible);
        var box = editor.GetVisualDescendants().OfType<TextInput>().Single();
        Assert.True(box.IsFocused, "the name box never took focus");
        Assert.DoesNotContain(Screen(window).GetVisualDescendants().OfType<TextBlock>(),
            t => t.Classes.Contains("albumName") && t.IsEffectivelyVisible);

        box.Text = "Songs to hum well";
        window.KeyPressQwerty(PhysicalKey.Enter, RawInputModifiers.None);
        Harness.Pump();

        Assert.Equal("Songs to hum well", harness.Vm.CurrentPlaylist?.Name);
        Assert.False(editor.IsEffectivelyVisible);
        TextBlockSaying(window, "Songs to hum well");
    }

    // Empty rather than borrowing the first song's cover: a playlist is not an
    // album, and the placeholder is what AlbumArtView draws for anything with
    // no art of its own.
    [AvaloniaFact]
    public void Its_cover_is_empty()
    {
        using var harness = new Harness(3);
        var window = harness.Window;

        var art = Assert.Single(Screen(window).GetVisualDescendants().OfType<SquareAlbumArtView>(), a => a.IsEffectivelyVisible);
        Assert.Null(art.AlbumArt);
        Assert.NotNull(art.GetVisualDescendants().OfType<AlbumArtPlaceholder>().FirstOrDefault(p => p.IsEffectivelyVisible));
        Assert.Null(harness.Vm.CurrentPlaylistHeader?.RepresentativeTrack);

    }

    // The header goes above the rows inside the list's own scroller, so it
    // scrolls away with them - and the list is still the virtualizing ListBox,
    // which is the whole reason it is put there rather than stacked above it.
    [AvaloniaFact]
    public void The_header_scrolls_with_the_songs()
    {
        using var harness = new Harness(3);
        var window = harness.Window;

        var name = TextBlockSaying(window, PlaylistName);
        var list = Assert.Single(Screen(window).GetVisualDescendants().OfType<ListBox>(), l => l.IsEffectivelyVisible);

        Assert.Same(list, name.FindAncestorOfType<ListBox>());
        Assert.NotNull(name.FindAncestorOfType<ScrollViewer>());

    }

    // An empty playlist used to get the "Nothing Here" overlay, centred over
    // the whole screen - which, now that the screen has a header saying whose
    // empty space this is, landed on top of it. The header is the answer to
    // the question the overlay was asking.
    [AvaloniaFact]
    public void An_empty_playlist_shows_its_header_rather_than_an_overlay()
    {
        using var harness = new Harness(0);
        var window = harness.Window;

        Assert.False(harness.Vm.IsContentEmpty);
        Assert.NotNull(TextBlockSaying(window, PlaylistName));
        Assert.DoesNotContain(window.GetVisualDescendants().OfType<TextBlock>(),
            t => t.IsEffectivelyVisible && t.Text == "Nothing Here");
    }

    // The buttons were there, bound and enabled, and a tap did nothing: a
    // playlist's header sits inside the track ListBox (ScreenScroll.Header),
    // and the list's drag-to-reorder handler tunnels over everything in it,
    // releasing the pointer capture on any release at all. That took the
    // pointer off the button before the button saw its own release, and a
    // Button that has lost capture raises no Click. An album's header, not
    // being inside a list, was never touched by it - so the same markup
    // worked there and nowhere else.
    [AvaloniaFact]
    public void A_tap_on_the_header_buttons_reaches_them()
    {
        using var harness = new Harness(3);
        var window = harness.Window;

        Tap(window, HeaderButton(window, MaterialIconKind.Shuffle));
        Assert.True(harness.Vm.PlaylistControl.IsShuffleEnabled, "shuffle did not answer the tap");

        Tap(window, HeaderButton(window, MaterialIconKind.Pencil));
        Assert.True(harness.Vm.CurrentPlaylistItem?.IsEditing, "the pencil did not answer the tap");
    }

    private static Button HeaderButton(Window window, MaterialIconKind kind) =>
        Screen(window).GetVisualDescendants().OfType<MaterialIcon>()
            .Where(i => i.IsEffectivelyVisible && i.Kind == kind)
            .Select(i => i.FindAncestorOfType<Button>()!)
            .First(b => b.Classes.Contains("pill"));

    private static void Tap(Window window, Button button)
    {
        var middle = button.TranslatePoint(new Point(button.Bounds.Width / 2, button.Bounds.Height / 2), window) ?? default;
        window.MouseDown(middle, MouseButton.Left);
        Harness.Pump(60);
        window.MouseUp(middle, MouseButton.Left);
        Harness.Pump(120);
    }

    // The screen's title line would otherwise say the playlist's name in the
    // band as well as in the header right below it.
    [AvaloniaFact]
    public void The_name_is_not_also_in_the_title_line()
    {
        using var harness = new Harness(3);
        Assert.Equal(string.Empty, harness.Vm.CurrentFrame.Title);
    }
}
