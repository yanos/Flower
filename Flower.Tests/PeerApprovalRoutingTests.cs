using System.Collections.Generic;
using System.Threading.Tasks;

using Avalonia.Headless.XUnit;
using Avalonia.Threading;

using Flower.Models;
using Flower.Services;
using Flower.Tests.TestSupport;

using Xunit;

namespace Flower.Tests;

// The approve/deny prompt's route from SyncHttpServer up to whatever UI is
// listening - the half of docs/ARCHITECTURE-REVIEW.md Tier 5.6 that
// SyncHttpServerRoundTripTests does not reach. That suite drives the *server*
// over a real socket with a real keypair; what is untested is MainViewModel's
// own handler, and specifically that it fails closed rather than hanging when
// nothing is listening above it.
[Collection("PlatformDataDirectory")]
public class PeerApprovalRoutingTests : PinnedDataDirectory
{
    private static MainViewModelHarness.Parts Build() =>
        MainViewModelHarness.BuildParts(new Library(new List<Track>()), new MainPlaylist(new List<Track>()));

    // MainViewModel always subscribes to SyncHttpServer, so the server's own
    // "no UI listening" fallback never fires in the app - MainViewModel's
    // does, and it has to fail the same way. On mobile this is not
    // hypothetical: MainViewModel is constructed but MainView, the only
    // subscriber, never is.
    [AvaloniaFact]
    public async Task An_approval_request_nobody_is_listening_for_is_denied_rather_than_left_hanging()
    {
        using var parts = Build();

        var approved = await parts.SyncHttpServer.RequestApprovalAsync("stranger-fp", "Stranger");

        Assert.False(approved);
    }

    [AvaloniaFact]
    public async Task An_approval_the_UI_grants_is_reported_back_as_granted()
    {
        using var parts = Build();
        PeerApprovalRequestedEventArgs? seen = null;
        parts.Main.PeerApprovalRequested += (_, e) =>
        {
            seen = e;
            e.Resolution.TrySetResult(true);
        };

        var pending = parts.SyncHttpServer.RequestApprovalAsync("peer-fp", "Kitchen");
        Dispatcher.UIThread.RunJobs(); // the handler marshals the prompt to the UI thread

        Assert.True(await pending);
        Assert.Equal("peer-fp", seen?.Fingerprint);
        Assert.Equal("Kitchen", seen?.Alias);
    }

    [AvaloniaFact]
    public async Task An_approval_the_UI_refuses_is_reported_back_as_refused()
    {
        using var parts = Build();
        parts.Main.PeerApprovalRequested += (_, e) => e.Resolution.TrySetResult(false);

        var pending = parts.SyncHttpServer.RequestApprovalAsync("peer-fp", "Kitchen");
        Dispatcher.UIThread.RunJobs();

        Assert.False(await pending);
    }

    // A disposed MainViewModel has let go of the server, so its prompt no
    // longer reaches a UI that is no longer there - and the request must still
    // settle, closed, rather than waiting out the approval timeout.
    [AvaloniaFact]
    public async Task A_disposed_MainViewModel_no_longer_answers_for_the_server()
    {
        using var parts = Build();
        parts.Main.PeerApprovalRequested += (_, e) => e.Resolution.TrySetResult(true);

        parts.Main.Dispose();
        var approved = await parts.SyncHttpServer.RequestApprovalAsync("peer-fp", "Kitchen");

        Assert.False(approved);
    }
}
