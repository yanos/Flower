using System.Runtime.CompilerServices;

using Flower.Audio;

using Miniaudio;

namespace Flower.Tests;

public class MiniaudioIosAudioSessionTests
{
    // These call miniaudio directly (ma.context_config_init) before touching
    // MiniaudioSink - and on iOS it is MiniaudioSink's static constructor that
    // registers the resolver finding miniaudio inside its embedded framework.
    // So they passed only when an earlier test had already touched the sink,
    // and a CI simulator ordering them first failed both with
    // DllNotFoundException. The app is not exposed: MiniaudioSink is the only
    // caller of ma.*, so its constructor always runs first.
    public MiniaudioIosAudioSessionTests()
    {
        RuntimeHelpers.RunClassConstructor(typeof(MiniaudioSink).TypeHandle);
    }

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
