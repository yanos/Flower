using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

using Flower.Services;

using Xunit;

namespace Flower.Tests.Network;

// A link opened while Flower already runs reaches a second process on Windows
// and Linux, which hands it to the first and exits instead of opening a second
// window. Each test listens on a pipe of its own, so none of them talks to a
// Flower the developer has open.
public class PairingLinkForwarderTests
{
    private static readonly string Link =
        new PairingInvite(["https://38.133.38.247:4534"], "YRR5M", "2ba46d92").ToString();

    private static string UniquePipe() => "flower-links-test-" + Guid.NewGuid().ToString("N")[..12];

    [Fact]
    public async Task A_link_offered_to_a_running_instance_arrives_in_its_inbox()
    {
        var pipe = UniquePipe();
        var received = new TaskCompletionSource<PairingInvite>(TaskCreationOptions.RunContinuationsAsynchronously);
        var inbox = new PairingLinkInbox(post: action => action());
        inbox.Attach(invite => received.TrySetResult(invite));
        using var stop = new CancellationTokenSource();
        var listening = PairingLinkForwarder.ListenAsync(inbox, pipeName: pipe, ct: stop.Token);

        var forwarded = false;
        for (var attempt = 0; attempt < 20 && !forwarded; attempt++)
            forwarded = PairingLinkForwarder.TryForward(Link, TimeSpan.FromMilliseconds(250), pipe);

        Assert.True(forwarded);
        var invite = await received.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        Assert.Equal("YRR5M", invite.Code);

        stop.Cancel();
        await listening.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
    }

    // With nothing running, the launch that was handed the link is the
    // instance, and keeps it.
    [Fact]
    public void With_no_instance_running_the_link_is_not_forwarded()
    {
        Assert.False(PairingLinkForwarder.TryForward(Link, TimeSpan.FromMilliseconds(100), UniquePipe()));
    }
}
