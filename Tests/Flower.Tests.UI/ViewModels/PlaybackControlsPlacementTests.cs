using System.Linq;

using Avalonia.Headless.XUnit;

using Flower.Models;
using Flower.Persistence;
using Flower.Tests.TestSupport;

using Xunit;

namespace Flower.Tests.ViewModels;

// The desktop's two places for the player bar (AppSettings.
// PlaybackControlsPlacement) - the view model's half: that the choice is
// remembered. Where MainView.axaml puts the bar is the view's business and
// not tested.
[Collection("PlatformDataDirectory")]
public class PlaybackControlsPlacementTests : PinnedDataDirectory
{
    private static MainViewModelHarness.Parts Build(PlaybackControlsPlacement placement)
    {
        var tracks = Enumerable.Range(0, 6).Select(i => new Track
        {
            Title = $"Song {i}", Path = $"/music/{i}.mp3", Album = "An Album", Artists = "An Artist",
        }).ToList();
        var parts = MainViewModelHarness.BuildParts(new Library(tracks), new MainPlaylist(tracks));
        parts.Main.PlaybackControlsPlacement = placement;
        return parts;
    }

    [AvaloniaFact]
    public void Switching_back_and_forth_is_remembered()
    {
        using var parts = Build(PlaybackControlsPlacement.Top);

        parts.Main.TogglePlaybackControlsPlacementCommand!.Execute(null);
        Assert.Equal(PlaybackControlsPlacement.Bottom, parts.AppSettings.PlaybackControlsPlacement);

        parts.Main.TogglePlaybackControlsPlacementCommand!.Execute(null);
        Assert.Equal(PlaybackControlsPlacement.Top, parts.AppSettings.PlaybackControlsPlacement);
    }
}
