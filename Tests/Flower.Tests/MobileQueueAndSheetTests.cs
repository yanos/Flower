using System;
using System.Linq;
using System.Threading;

using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Media;
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

using Xunit;

namespace Flower.Tests;

// The phone's whole main view, laid out for real, for what only a laid-out
// view shows - each test says which.
[Collection("PlatformDataDirectory")]
public class MobileQueueAndSheetTests : PinnedDataDirectory
{
    private const double Width = 390;
    private const double Height = 800;

    private sealed class Harness : IDisposable
    {
        public Window Window { get; }
        public MobileMainView View { get; }
        public MobileMainViewModel Vm => _parts.Mobile;
        private readonly MainViewModelHarness.MobileParts _parts;

        public Harness()
        {
            var tracks = Enumerable.Range(0, 60).Select(i => new Track
            {
                Title = $"Track {i:D2}", Path = $"/music/{i}.mp3",
                Album = $"Album {i / 10}", Artists = $"Artist {i / 10}",
            }).ToList();

            _parts = MainViewModelHarness.BuildMobile(new Library(tracks), new MainPlaylist(tracks));
            View = new MobileMainView { DataContext = Vm };
            // A theme for the templates and the app's resources for its
            // brushes and sizes - see MobileTabBarLayoutTests.
            Window = new Window { Width = Width, Height = Height };
            Window.Styles.Add(new FluentTheme());
            Window.Resources.MergedDictionaries.Add(new Avalonia.Markup.Xaml.Styling.ResourceInclude(new Uri("avares://Flower/"))
            {
                Source = new Uri("avares://Flower/Theme.axaml"),
            });
            Window.Content = View;
            Window.Show();
            Pump();
        }

        // Long enough for a screen's entrance or a sheet's slide (280ms) to
        // land, on the real clock.
        public static void Pump(int milliseconds = 600)
        {
            using var cts = new CancellationTokenSource(milliseconds);
            Dispatcher.UIThread.MainLoop(cts.Token);
        }

        public void PlayFrom(int index)
        {
            var tracks = _parts.Parts.Library.Tracks.ToList();
            _parts.Parts.Main.SetPlayQueue(tracks);
            _parts.Parts.PlaylistControl.Play(tracks[index], index);
            Dispatcher.UIThread.RunJobs();
        }

        public T Showing<T>() where T : Control =>
            Window.GetVisualDescendants().OfType<T>().First(c => c.IsEffectivelyVisible);

        public void Dispose()
        {
            Pump(100);
            Window.Close();
            _parts.Dispose();
        }
    }

    // Opened, the Queue shows the song playing at its top - just under the
    // header, with a sliver of the song before it - not the top of what has
    // played, thirty songs up.
    [AvaloniaFact]
    public void The_queue_opens_on_the_song_playing()
    {
        using var h = new Harness();
        h.PlayFrom(30);

        h.Vm.SelectTabCommand.Execute(nameof(MobileTab.Queue));
        Harness.Pump();

        var screen = h.Showing<QueueScreenView>();
        var scroller = screen.GetVisualDescendants().OfType<ScrollViewer>().First();
        Assert.Equal(30, h.Vm.QueuePlayedCount);
        Assert.True(scroller.Offset.Y > 0, $"scrolled to {scroller.Offset.Y}");

        var playing = screen.GetVisualDescendants().OfType<Button>()
            .First(b => b.Classes.Contains("trackRow") && b.DataContext is TrackRowViewModel { IsCurrentlyPlaying: true });
        var top = playing.TranslatePoint(default, scroller)!.Value.Y;
        Assert.InRange(top, 0, 200);
    }

    // Fluent shrinks a pressed button a little; on a full-width row that read
    // as the list twitching under every touch.
    [AvaloniaFact]
    public void A_song_row_keeps_its_size_while_pressed()
    {
        using var h = new Harness();
        h.Vm.SelectTabCommand.Execute(nameof(MobileTab.Songs));
        Harness.Pump();

        var row = h.Showing<TrackListScreenView>().GetVisualDescendants().OfType<Button>()
            .First(b => b.Classes.Contains("trackRow"));
        ((IPseudoClasses)row.Classes).Set(":pressed", true);
        // Fluent eases the shrink in rather than snapping to it.
        Harness.Pump(300);

        Assert.True(row.RenderTransform is null || row.RenderTransform.Value.IsIdentity,
            $"pressed row transformed by {row.RenderTransform?.Value}");
    }

    // A phone keyboard has no key that puts it away: a tap anywhere on Track
    // Info that is not a box does, and a tap on a box keeps it.
    [AvaloniaFact]
    public void Track_info_puts_the_keyboard_away_on_a_tap_off_the_boxes()
    {
        using var h = new Harness();
        h.Vm.SelectTabCommand.Execute(nameof(MobileTab.Songs));
        Harness.Pump();
        h.Vm.OpenTrackActionsCommand.Execute(h.Showing<TrackListScreenView>().GetVisualDescendants().OfType<Button>()
            .First(b => b.Classes.Contains("trackRow")).DataContext);
        h.Vm.ViewTrackInfoCommand.Execute(null);
        Harness.Pump();

        var info = h.Showing<TrackInfoView>();
        var title = info.FindControl<TextInput>("TitleBox")!;
        var artist = info.FindControl<TextInput>("ArtistBox")!;
        var focus = TopLevel.GetTopLevel(info)!.FocusManager!;

        title.Focus();
        Tap(artist);
        Assert.Same(title, focus.GetFocusedElement());

        var card = info.GetVisualDescendants().OfType<Border>().First(b => b.Classes.Contains("card"));
        Tap(card);
        Assert.Null(focus.GetFocusedElement());
    }

    private static void Tap(Control target)
    {
        var pointer = new Pointer(Pointer.GetNextFreeId(), PointerType.Touch, true);
        var root = TopLevel.GetTopLevel(target)!;
        var released = new PointerReleasedEventArgs(target, pointer, root, default, 0,
            new PointerPointProperties(RawInputModifiers.None, PointerUpdateKind.LeftButtonReleased), KeyModifiers.None, MouseButton.Left);
        target.RaiseEvent(new TappedEventArgs(InputElement.TappedEvent, released));
    }
}
