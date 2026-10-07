using Flower.ViewModels;

using Material.Icons;

using Xunit;

namespace Flower.Tests;

// The status bar's one volume icon says how loud it is: an X at nothing, no
// waves near the start, one in the middle, two toward the top.
public class VolumeIconTests
{
    [Theory]
    [InlineData(0, MaterialIconKind.VolumeMute)]
    [InlineData(1, MaterialIconKind.VolumeLow)]
    [InlineData(33, MaterialIconKind.VolumeLow)]
    [InlineData(34, MaterialIconKind.VolumeMedium)]
    [InlineData(66, MaterialIconKind.VolumeMedium)]
    [InlineData(67, MaterialIconKind.VolumeHigh)]
    [InlineData(100, MaterialIconKind.VolumeHigh)]
    public void The_icon_follows_the_level(int volume, MaterialIconKind expected) =>
        Assert.Equal(expected, VolumeControlViewModel.IconFor(volume));
}
