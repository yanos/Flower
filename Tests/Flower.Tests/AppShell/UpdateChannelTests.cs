using Flower.Services;

using Xunit;

namespace Flower.Tests.AppShell;

// Which tags each update channel offers: releases to everyone, betas only to
// the beta channel, and every other pre-release - an -alpha, an -rc, a CI
// build's version - to nobody.
public class UpdateChannelTests
{
    [Theory]
    [InlineData("v0.12.0")]
    [InlineData("0.12.0")]
    [InlineData("v0.12.0+abc1234")]
    public void A_release_is_offered_on_both(string tag)
    {
        Assert.True(UpdateChannel.Stable.Offers(tag));
        Assert.True(UpdateChannel.Beta.Offers(tag));
    }

    [Theory]
    [InlineData("v0.12.0-beta")]
    [InlineData("v0.12.0-beta.1")]
    [InlineData("v0.12.0-beta.2+abc1234")]
    public void A_beta_is_offered_only_on_the_beta_channel(string tag)
    {
        Assert.False(UpdateChannel.Stable.Offers(tag));
        Assert.True(UpdateChannel.Beta.Offers(tag));
    }

    [Theory]
    [InlineData("v0.12.0-alpha.1")]
    [InlineData("v0.12.0-rc.1")]
    [InlineData("v0.12.0-alpha.0.31")]   // MinVer's untagged build
    [InlineData("v0.12.0-betamax")]
    [InlineData("v0.12.0-ci.beta")]
    [InlineData("music_audioset_epoch_15")]
    public void Any_other_tag_is_offered_on_neither(string tag)
    {
        Assert.False(UpdateChannel.Stable.Offers(tag));
        Assert.False(UpdateChannel.Beta.Offers(tag));
    }
}
