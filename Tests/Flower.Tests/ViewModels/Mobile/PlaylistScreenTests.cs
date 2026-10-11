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

namespace Flower.Tests.ViewModels.Mobile;

// A playlist's own tracks are a screen about one thing, the way an album's
// are, and show the album screen's header: the count of its songs where an
// artist's name goes, its name in the header rather than the title line.
[Collection("PlatformDataDirectory")]
public class PlaylistScreenTests : PinnedDataDirectory
{
    public PlaylistScreenTests() => TestIoc.EnsureConfigured();

    private const string PlaylistName = "Songs to hum badly";

    private static Track SomeTrack(string title) => new()
    {
        Title = title,
        Album = "Bee Thousand",
        Artists = "Guided by Voices",
        Path = "/music/" + title + ".flac",
        DateAdded = DateTimeOffset.UtcNow,
    };

    private static Library LibraryWithPlaylist(int songs)
    {
        var tracks = Enumerable.Range(0, songs).Select(i => SomeTrack("Song " + i)).ToList();
        var library = new Library(tracks);
        library.AddPlaylist(new Playlist(PlaylistName, tracks));
        return library;
    }

    // Drilled into the playlist, with no view at all.
    private static MainViewModelHarness.MobileParts DrilledIn(int songs)
    {
        var parts = MainViewModelHarness.BuildMobile(LibraryWithPlaylist(songs), new MainPlaylist(new List<Track>()));
        var vm = parts.Mobile;
        vm.SelectTabCommand.Execute(nameof(MobileTab.Playlists));
        UiWait.Settle(() => vm.PlaylistPickerItems.Any(i => i.Name == PlaylistName), "the playlist never reached the picker");
        vm.SelectPlaylistCommand.Execute(vm.PlaylistPickerItems.First(i => i.Name == PlaylistName));
        UiWait.Settle(() => vm.IsShowingPlaylistTracks && vm.CurrentPlaylistHeader != null, "never drilled into the playlist");
        return parts;
    }

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
            _parts = MainViewModelHarness.BuildMobile(LibraryWithPlaylist(songs), new MainPlaylist(new List<Track>()));
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

    // One song is not "1 songs".
    [AvaloniaFact]
    public void One_song_is_counted_in_the_singular()
    {
        using var scope = DrilledIn(1);
        Assert.Equal("1 song", scope.Mobile.CurrentPlaylistHeader?.Artist);
    }

    // An empty playlist used to get the "Nothing Here" overlay, centred over
    // the whole screen - which landed on top of the header saying whose empty
    // space this is. The header is the answer to the question the overlay was
    // asking.
    [AvaloniaFact]
    public void An_empty_playlist_is_not_an_empty_screen()
    {
        using var scope = DrilledIn(0);

        Assert.False(scope.Mobile.IsContentEmpty);
        Assert.Equal(PlaylistName, scope.Mobile.CurrentPlaylistHeader?.Name);
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
        using var scope = DrilledIn(3);
        Assert.Equal(string.Empty, scope.Mobile.CurrentFrame.Title);
    }
}
