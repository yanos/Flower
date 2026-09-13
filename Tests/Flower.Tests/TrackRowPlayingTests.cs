using System.Linq;

using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Media;
using Avalonia.VisualTree;

using Flower.Controls;
using Flower.Models;
using Flower.Persistence;
using Flower.Tests.TestSupport;
using Flower.ViewModels;

using Xunit;

namespace Flower.Tests;

// The main track list says "this is the song playing" with weight and nothing
// else (see TrackRowControl.axaml's .playing style). Asserted on the rendered
// TextBlocks rather than on the row's classes, because the class was being set
// correctly the whole time the style matching it was inert: the selector named
// UserControl, which is an exact-type match, and the row is a TrackRowControl.
// A test on Classes would have passed throughout and said nothing.
[Collection("PlatformDataDirectory")]
public class TrackRowPlayingTests : PinnedDataDirectory
{
    public TrackRowPlayingTests() => TestIoc.EnsureConfigured();

    private static FontWeight[] WeightsOf(bool playing)
    {
        var row = new TrackRowControl(new ColumnManager(new AppSettings(), new AppSettingsStore()))
        {
            DataContext = new TrackRowViewModel
            {
                Track = new Track { Path = "/music/1.mp3", Title = "Nobody Speak" },
                IsCurrentlyPlaying = playing,
            },
        };

        var window = new Window { Width = 800, Height = 200, Content = row };
        window.Show();
        window.Measure(new Size(800, 200));
        window.Arrange(new Rect(0, 0, 800, 200));
        window.UpdateLayout();

        var weights = row.GetVisualDescendants().OfType<TextBlock>().Select(t => t.FontWeight).ToArray();
        window.Close();
        return weights;
    }

    [AvaloniaFact]
    public void The_playing_row_is_bold()
    {
        var weights = WeightsOf(playing: true);

        Assert.NotEmpty(weights);
        Assert.All(weights, w => Assert.Equal(FontWeight.Bold, w));
    }

    [AvaloniaFact]
    public void Every_other_row_is_not()
    {
        var weights = WeightsOf(playing: false);

        Assert.NotEmpty(weights);
        Assert.All(weights, w => Assert.Equal(FontWeight.Normal, w));
    }
}
