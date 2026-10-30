using System;
using System.Collections.Generic;

using Flower.Services;

using Xunit;

namespace Flower.Tests.Network;

// Where a flower:// link opened from outside the app waits for the UI that can
// ask about it - the link that launched the app arrives before any window does.
public class PairingLinkInboxTests
{
    private sealed class Clock : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = new(2026, 10, 30, 9, 0, 0, TimeSpan.Zero);

        public override DateTimeOffset GetUtcNow() => Now;
    }

    private static readonly string Link =
        new PairingInvite(["http://192.168.50.52:4533", "https://38.133.38.247:4534"], "YRR5M", "2ba46d92").ToString();

    private readonly Clock _clock = new();
    private readonly List<PairingInvite> _handled = [];
    private readonly PairingLinkInbox _inbox;

    public PairingLinkInboxTests()
    {
        _inbox = new PairingLinkInbox(post: action => action(), clock: _clock);
    }

    [Fact]
    public void A_link_that_arrives_before_the_ui_is_handed_over_once_it_attaches()
    {
        _inbox.Deliver(Link);
        Assert.Empty(_handled);

        _inbox.Attach(_handled.Add);

        Assert.Equal("YRR5M", Assert.Single(_handled).Code);
    }

    [Fact]
    public void A_link_that_arrives_afterwards_goes_straight_to_the_ui()
    {
        _inbox.Attach(_handled.Add);

        _inbox.Deliver(Link);

        Assert.Single(_handled);
    }

    [Theory]
    [InlineData("/Users/me/Music/song.flac")]
    [InlineData("https://example.com/")]
    [InlineData("flower://pair?code=YRR5M")]
    public void Anything_that_is_not_a_pairing_link_is_not_taken(string text)
    {
        _inbox.Attach(_handled.Add);

        Assert.False(_inbox.Deliver(text));
        Assert.Empty(_handled);
    }

    // A cold start on Android carries the link in the launch intent and in the
    // activation event both; a second confirmation for a code the first is
    // already spending could only fail.
    [Fact]
    public void One_link_seen_twice_in_quick_succession_is_handled_once()
    {
        _inbox.Attach(_handled.Add);

        _inbox.Deliver(Link);
        _clock.Now += TimeSpan.FromSeconds(2);
        _inbox.Deliver(Link);

        Assert.Single(_handled);
    }

    [Fact]
    public void The_same_link_opened_again_later_is_handled_again()
    {
        _inbox.Attach(_handled.Add);

        _inbox.Deliver(Link);
        _clock.Now += TimeSpan.FromMinutes(1);
        _inbox.Deliver(Link);

        Assert.Equal(2, _handled.Count);
    }

    // Windows and Linux start the registered command with the URL after it.
    [Fact]
    public void The_link_is_found_among_the_command_line_arguments()
    {
        _inbox.Attach(_handled.Add);

        Assert.True(_inbox.DeliverArguments(["--verbose", Link]));
        Assert.Single(_handled);
    }
}
