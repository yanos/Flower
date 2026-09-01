using Flower.Manager;

using Miniaudio;

namespace Flower.Tests;

public class MiniaudioIosAudioSessionTests
{
    [Fact]
    public void IosContextConfiguration_leavesTheSharedAudioSessionToFlower()
    {
        var contextConfig = ma.context_config_init();

        MiniaudioSink.ConfigureContextForPlatform(ref contextConfig, isIos: true);

        Assert.Equal(ma_ios_session_category.ma_ios_session_category_none, contextConfig.coreaudio.sessionCategory);
        Assert.Equal(1u, contextConfig.coreaudio.noAudioSessionActivate);
        Assert.Equal(1u, contextConfig.coreaudio.noAudioSessionDeactivate);
    }

    [Fact]
    public void NonIosContextConfiguration_doesNotChangeTheDefaultConfiguration()
    {
        var contextConfig = ma.context_config_init();
        var expected = contextConfig;

        MiniaudioSink.ConfigureContextForPlatform(ref contextConfig, isIos: false);

        Assert.Equal(expected.coreaudio.sessionCategory, contextConfig.coreaudio.sessionCategory);
        Assert.Equal(expected.coreaudio.noAudioSessionActivate, contextConfig.coreaudio.noAudioSessionActivate);
        Assert.Equal(expected.coreaudio.noAudioSessionDeactivate, contextConfig.coreaudio.noAudioSessionDeactivate);
    }
}
