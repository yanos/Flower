using System;
using System.Text.Json.Serialization;

namespace Flower.Services
{
    // Which releases a desktop is offered (AppSettings.UpdateChannel). The
    // server's image has the same two, as the `latest` and `beta` image tags
    // (scripts/image-channels.py), by the same rule.
    [JsonConverter(typeof(JsonStringEnumConverter<UpdateChannel>))]
    public enum UpdateChannel
    {
        // Releases only: v1.2.0.
        Stable,

        // Releases and betas, whichever is newer: v1.2.0 or v1.3.0-beta.1.
        // A tag with any other pre-release label (-alpha, -rc, a CI build's)
        // is offered to nobody, so one can be published to try out without
        // reaching anyone's update banner.
        Beta,
    }

    public static class UpdateChannels
    {
        // Whether `tag` is a release this channel offers. A tag that is not
        // a version at all is offered by neither.
        public static bool Offers(this UpdateChannel channel, string tag)
        {
            if (!ReleaseVersion.TryGetPreRelease(tag, out var preRelease))
                return false;
            if (preRelease is null)
                return true;
            return channel == UpdateChannel.Beta && IsBeta(preRelease);
        }

        // "beta", "beta.3": the label's first identifier is the word itself,
        // so "betamax" or "rc.1-beta" is not one.
        private static bool IsBeta(string preRelease)
        {
            var first = preRelease.Split('.')[0];
            return string.Equals(first, "beta", StringComparison.OrdinalIgnoreCase);
        }
    }
}
