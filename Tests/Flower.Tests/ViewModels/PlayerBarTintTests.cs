using System.Linq;

using Avalonia.Headless.XUnit;

using Flower.Models;
using Flower.Persistence;
using Flower.Tests.TestSupport;

using Xunit;

namespace Flower.Tests.ViewModels;

// The desktop's player bar washed in the album's colour (AppSettings.
// TintPlayerBarWithAlbumColor) - the view model's half: that the choice is
// on unless turned off, and remembered. Which colour, and how the bar
// draws over it, is the view's business and not tested.
[Collection("PlatformDataDirectory")]
public class PlayerBarTintTests : PinnedDataDirectory
{
    private static MainViewModelHarness.Parts Build()
    {
        var tracks = Enumerable.Range(0, 3).Select(i => new Track
        {
            Title = $"Song {i}", Path = $"/music/{i}.mp3", Album = "An Album", Artists = "An Artist",
        }).ToList();
        return MainViewModelHarness.BuildParts(new Library(tracks), new MainPlaylist(tracks));
    }

    [Fact]
    public void On_by_default()
    {
        Assert.True(new AppSettings().TintPlayerBarWithAlbumColor);
    }

    [AvaloniaFact]
    public void Turning_it_off_is_remembered()
    {
        using var parts = Build();

        parts.Main.TintPlayerBarWithAlbumColor = false;

        Assert.False(parts.AppSettings.TintPlayerBarWithAlbumColor);
    }
}
