using Flower.Services;

using Xunit;

namespace Flower.Tests.Network;

// A server's emoji is one character as a reader counts them - so an emoji
// made of several code points must survive whole, and anything after it goes.
public class ServerIconTests
{
    [Theory]
    [InlineData(" 🌻 ", "🌻")]
    [InlineData("🇫🇷 France", "🇫🇷")]
    [InlineData("👨‍👩‍👧 and more", "👨‍👩‍👧")]
    [InlineData("👍🏽👍", "👍🏽")]
    [InlineData("   ", "")]
    [InlineData(null, "")]
    public void An_emoji_is_kept_whole_and_alone(string? typed, string kept) =>
        Assert.Equal(kept, ServerIcon.CleanEmoji(typed));
}
