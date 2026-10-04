using System;
using System.Linq;

using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;

using Flower.Models;
using Flower.Persistence;
using Flower.Tests.TestSupport;
using Flower.UserControls;
using Flower.Views;

using Xunit;

namespace Flower.Tests;

// The desktop's two arrangements of the transport (AppSettings.
// PlaybackControlsPlacement): the top bar, or the status bar plus a card at the
// foot of the sidebar. Held down as what is on screen, because the switch is
// made by styles and visibility in MainView.axaml, where a renamed element or a
// local value outranking a style fails silently - the window still opens, with
// both transports or neither.
[Collection("PlatformDataDirectory")]
public class PlaybackControlsPlacementTests : PinnedDataDirectory
{
    public PlaybackControlsPlacementTests() => TestIoc.EnsureConfigured();

    private static (Window Window, MainView View, MainViewModelHarness.Parts Parts) Show(PlaybackControlsPlacement placement)
    {
        var tracks = Enumerable.Range(0, 6).Select(i => new Track
        {
            Title = $"Song {i}", Path = $"/music/{i}.mp3", Album = "An Album", Artists = "An Artist",
        }).ToList();
        var parts = MainViewModelHarness.BuildParts(new Library(tracks), new MainPlaylist(tracks));
        parts.Main.PlaybackControlsPlacement = placement;

        var view = new MainView { DataContext = parts.Main };
        var window = new Window { Width = 1100, Height = 720, Content = view };
        window.Show();
        parts.PlaylistControl.Play(tracks[0]);
        Dispatcher.UIThread.RunJobs();
        return (window, view, parts);
    }

    private static bool OnScreen(Control control) =>
        control.GetSelfAndVisualAncestors().OfType<Control>().All(c => c.IsVisible);

    [AvaloniaFact]
    public void At_the_top_there_is_one_transport_and_it_is_in_the_top_bar()
    {
        var (window, view, parts) = Show(PlaybackControlsPlacement.Top);
        using var _ = parts;

        Assert.True(OnScreen(view.FindControl<PlaylistControls>("PlaybackControls")!));
        Assert.False(OnScreen(view.FindControl<PlaylistControls>("BottomPlaybackControls")!));
        window.Close();
    }

    [AvaloniaFact]
    public void At_the_bottom_the_transport_is_in_the_status_bar_under_the_whole_window()
    {
        var (window, view, parts) = Show(PlaybackControlsPlacement.Bottom);
        using var _ = parts;

        Assert.False(OnScreen(view.FindControl<PlaylistControls>("PlaybackControls")!));
        Assert.True(OnScreen(view.FindControl<PlaylistControls>("BottomPlaybackControls")!));

        // Under the sidebar as well as the page, which is a style's doing.
        var statusBar = view.FindControl<Border>("StatusBar")!;
        Assert.Equal(0, Grid.GetColumn(statusBar));
        Assert.Equal(3, Grid.GetColumnSpan(statusBar));
        Assert.Equal(1, Grid.GetRowSpan(view.FindControl<Border>("Sidebar")!));
        window.Close();
    }

    [AvaloniaFact]
    public void Switching_back_and_forth_moves_it_and_is_remembered()
    {
        var (window, view, parts) = Show(PlaybackControlsPlacement.Top);
        using var _ = parts;

        parts.Main.TogglePlaybackControlsPlacementCommand!.Execute(null);
        Dispatcher.UIThread.RunJobs();
        Assert.True(OnScreen(view.FindControl<PlaylistControls>("BottomPlaybackControls")!));
        Assert.Equal(PlaybackControlsPlacement.Bottom, parts.AppSettings.PlaybackControlsPlacement);

        parts.Main.TogglePlaybackControlsPlacementCommand!.Execute(null);
        Dispatcher.UIThread.RunJobs();
        Assert.True(OnScreen(view.FindControl<PlaylistControls>("PlaybackControls")!));
        Assert.Equal(2, Grid.GetRowSpan(view.FindControl<Border>("Sidebar")!));
        window.Close();
    }
}
