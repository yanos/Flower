using Flower.Persistence;

using Xunit;

namespace Flower.Tests.Persistence;

// A device's alias is its own text, kept in trusted-peers.json and written
// into the server's log - docs/CODE-REVIEW-2026-10.md A5.
public class DeviceAliasTests
{
    [Fact]
    public void A_newline_cannot_start_a_forged_log_line()
    {
        Assert.Equal("Kitchen iPadPaired Mallory (fp) from 10.0.0.1. Admin: True.",
            DeviceAlias.Clean("Kitchen iPad\nPaired Mallory (fp) from 10.0.0.1. Admin: True."));
    }

    [Fact]
    public void A_bidirectional_override_cannot_reverse_the_name()
    {
        Assert.Equal("iPhone", DeviceAlias.Clean("‮iPhone‬"));
    }

    [Fact]
    public void A_long_name_is_cut_without_splitting_an_emoji()
    {
        var cleaned = DeviceAlias.Clean(new string('a', DeviceAlias.MaxLength - 1) + "🎵🎵");

        Assert.Equal(new string('a', DeviceAlias.MaxLength - 1), cleaned);
    }

    [Fact]
    public void A_name_with_emoji_and_joiners_is_kept_whole()
    {
        Assert.Equal("👨‍👩‍👧 Family iPad", DeviceAlias.Clean("👨‍👩‍👧 Family iPad"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("  \t\r\n ")]
    [InlineData("‮⁦")]
    public void Nothing_usable_reads_as_no_alias(string? claimed)
    {
        Assert.Null(DeviceAlias.Clean(claimed));
    }
}
