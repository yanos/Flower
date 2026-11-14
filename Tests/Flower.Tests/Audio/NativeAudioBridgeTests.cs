using Flower.Audio;

namespace Flower.Tests.Audio;

public class NativeAudioBridgeTests
{
    [Theory]
    [InlineData(DeviceSampleFormat.S16, int.MaxValue / 8 + 1)]
    [InlineData(DeviceSampleFormat.S24, int.MaxValue / 8 + 1)]
    [InlineData(DeviceSampleFormat.S16, int.MaxValue)]
    [InlineData(DeviceSampleFormat.S24, int.MaxValue)]
    public void Rejects_capacities_that_cannot_be_expressed_as_signed_byte_counts(
        DeviceSampleFormat format, int capacityFrames)
    {
        Assert.True(NativeAudioBridge.IsAvailable);

        // The ring holds stereo float32 even when the device renders S16.
        using var bridge = NativeAudioBridge.TryCreate(capacityFrames, format, 48000);

        Assert.Null(bridge);
    }
}
