using System;
using System.Collections.Generic;
using System.Linq;

using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Themes.Fluent;
using Avalonia.Threading;
using Avalonia.VisualTree;

using Flower.Controls;
using Flower.Models;
using Flower.Services;
using Flower.ViewModels;

using Flower.Tests.TestSupport;
using Flower.ViewModels.Mobile;

using CommunityToolkit.Mvvm.Input;

using Material.Icons;
using Material.Icons.Avalonia;

using Xunit;

namespace Flower.Tests.Views;

// The one download control every download icon in the app now is - a desktop
// row, a phone's row, an album tile, and mobile's top-bar "download all",
// which used to be its own hand-copied pair of glyphs that neither spun nor
// went away. These assert the sequence that copy got wrong, on the real
// control, over the real view-model state.
public class TrackDownloadButtonTests
{
    private static TrackRowViewModel Row()
    {
        var row = new TrackRowViewModel
        {
            Track = new Track { Title = "A", Path = null },
            // No dispatcher timer behind it, so a spinner started here doesn't
            // leave the shared 60Hz clock running past the test.
            Clock = new AnimationClock(() => TimeSpan.Zero),
        };
        row.IsDownloadable = true;
        return row;
    }

    private static (TrackDownloadButton Button, Window Window) Show(TrackRowViewModel row, double iconSize)
    {
        var button = new TrackDownloadButton { DataContext = row, IconSize = iconSize };
        var window = new Window { Content = button };
        window.Show();
        return (button, window);
    }


    // The defect this control shipped with the day it grew a Command: pressing
    // it lit the button up and did nothing else. Avalonia's Button raises Click
    // and only then runs its own Command, guarded by "if (!e.Handled)" - and
    // this control marks every click handled so the row underneath does not
    // also act on it. So the command has to be run by hand (see OnClick), and
    // this taps the real button rather than invoking anything, because the
    // whole bug lived in what Button does between the two.
    [AvaloniaFact]
    public void Pressing_it_runs_the_command_its_host_handed_over()
    {
        var row = Row();
        var pressedWith = new List<object?>();
        var button = new TrackDownloadButton
        {
            DataContext = row,
            IconSize = 16,
            Command = new RelayCommand<object?>(p => pressedWith.Add(p)),
            CommandParameter = row,
        };
        var window = new Window { Width = 200, Height = 100 };
        window.Styles.Add(new FluentTheme());
        window.Content = button;
        window.Show();
        window.UpdateLayout();
        // Hit testing reads bounds the render pass computes, so an unrendered
        // window's controls cannot be pressed at all.
        window.CaptureRenderedFrame();
        Dispatcher.UIThread.RunJobs();

        var inner = button.GetVisualDescendants().OfType<Button>().Single();
        var at = inner.TranslatePoint(new Point(inner.Bounds.Width / 2, inner.Bounds.Height / 2), window)!.Value;
        window.MouseMove(at);
        window.MouseDown(at, MouseButton.Left);
        window.MouseUp(at, MouseButton.Left);
        Dispatcher.UIThread.RunJobs();

        Assert.Same(row, Assert.Single(pressedWith));
    }
}

// The album/playlist header asks for the same control, over an indicator that
// stands for the whole screen (see MobileMainViewModel.DownloadAllIndicator).
// It sat in the top bar until that band was cleared; what is worth asserting
// either way is the wiring the compiler cannot check: which DataContext the
// control ends up on, and that its command resolves off the screen's own
// ancestor binding rather than off the indicator it is bound to.
[Collection("PlatformDataDirectory")]
public class DetailHeaderDownloadAllTests : PinnedDataDirectory
{
    public DetailHeaderDownloadAllTests() => TestIoc.EnsureConfigured();

    [AvaloniaFact]
    public void The_headers_download_all_is_the_shared_control_over_the_shared_indicator()
    {
        var tracks = new List<Track> { new() { Title = "A", Album = "An Album", Path = "/music/a.flac" } };
        using var parts = MainViewModelHarness.BuildMobile(new Library(tracks), new MainPlaylist(tracks));
        parts.Mobile.SelectTabCommand.Execute(nameof(MobileTab.Albums));
        parts.Mobile.SelectAlbumOrArtistCommand.Execute("An Album");
        MainViewModelHarness.WaitForTheDrillIn(parts.Mobile, "An Album");

        var view = new Flower.Views.Mobile.Screens.TrackListScreenView { DataContext = parts.Mobile };
        var window = new Window { Width = 390, Height = 700 };
        window.Styles.Add(new FluentTheme());
        window.Content = view;
        window.Show();
        view.ObserveLive(parts.Mobile);
        window.Measure(new Size(390, 700));
        window.Arrange(new Rect(0, 0, 390, 700));
        window.UpdateLayout();
        Dispatcher.UIThread.RunJobs();

        var button = view.GetVisualDescendants().OfType<TrackDownloadButton>()
            .Single(b => b.Label == "Download all");

        Assert.Same(parts.Mobile.DownloadAllIndicator, button.DataContext);
        Assert.Same(parts.Mobile.DownloadAllVisibleCommand, button.Command);
        // Nothing to fetch on a fully local library, so the icon well is empty
        // - the bug this replaces was a button that stayed up regardless.
        Assert.False(button.IsVisible);

        window.Close();
    }
}
