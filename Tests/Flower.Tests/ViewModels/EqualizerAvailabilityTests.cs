using System.Collections.Generic;

using Avalonia.Headless.XUnit;

using Flower.Audio;
using Flower.Models;
using Flower.Tests.TestSupport;

namespace Flower.Tests.ViewModels;

// The browser's player cannot equalize (IAudioManager.Features), and the
// equalizer used to open there anyway, every slider moving nothing.
[Collection("PlatformDataDirectory")]
public class EqualizerAvailabilityTests : PinnedDataDirectory
{
    [AvaloniaTheory]
    [InlineData(AudioFeatures.All, true)]
    [InlineData(AudioFeatures.None, false)]
    [InlineData(AudioFeatures.Gapless | AudioFeatures.OutputDevices, false)]
    public void The_equalizer_is_offered_only_by_a_player_that_can_equalize(AudioFeatures features, bool offered)
    {
        var library = new Library(new List<Track>());
        var parts = Own(MainViewModelHarness.Build(library, new MainPlaylist(library.Tracks)));

        parts.Audio.Features = features;

        Assert.Equal(offered, parts.Main.OpenEqualizerWindowCommand!.CanExecute(null));
    }
}
