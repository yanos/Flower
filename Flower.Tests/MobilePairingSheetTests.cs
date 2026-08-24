using System;
using System.Collections.Generic;
using System.Net;
using System.Threading.Tasks;

using Avalonia.Headless.XUnit;
using Avalonia.Threading;

using Flower.Models;
using Flower.Services;
using Flower.Tests.TestSupport;
using Flower.ViewModels;
using Flower.ViewModels.Mobile;

using Microsoft.Extensions.Logging.Abstractions;

using Xunit;

namespace Flower.Tests;

// The pairing sheet used to close the instant "Pair" was tapped, which meant a
// redeem that came back refused took the only surface that could have said so
// down with it - on a phone the pairing simply appeared not to have happened,
// with no message anywhere. It now holds itself open across the round trip; see
// MobileMainViewModel.SettleCodePairing, and PeerPairingService.
// DescribeRejectionAsync for where the wording comes from.
[Collection("PlatformDataDirectory")]
public class MobilePairingSheetTests : PinnedDataDirectory
{
    // Loopback, port 1: nothing is listening, so the redeem is refused within
    // milliseconds instead of sitting out PeerPairingService's 75s timeout.
    private static DiscoveredDevice UnreachableHeadlessServer() =>
        new()
        {
            InstanceName = "attic",
            BaseUri = NetworkDiscoveryService.HttpOrigin(new IPEndPoint(IPAddress.Loopback, 1)),
            Alias = "Attic",
            Fingerprint = "fp-attic",
            DeviceType = "server",
        };

    private static MainViewModelHarness.MobileParts Build()
    {
        var parts = MainViewModelHarness.BuildParts(new Library(new List<Track>()), new MainPlaylist(new List<Track>()));
        var mobile = new MobileMainViewModel(parts.Main, parts.PlaylistControl, parts.CurrentlyPlaying, NullLogger<MobileMainViewModel>.Instance);
        Dispatcher.UIThread.RunJobs();
        return new MainViewModelHarness.MobileParts(mobile, parts);
    }

    private static void OpenSheetFor(MobileMainViewModel mobile, DiscoveredDevice device, string code)
    {
        mobile.PairWithServerCommand.Execute(device);
        mobile.PendingPairingCode = code;
    }

    [AvaloniaFact]
    public void The_sheet_stays_up_while_the_code_is_in_flight()
    {
        using var scope = Build();
        OpenSheetFor(scope.Mobile, UnreachableHeadlessServer(), "ABCD2345");

        scope.Mobile.ConfirmPairServerCommand.Execute(null);

        Assert.True(scope.Mobile.IsShowingConfirmPairServer);
        Assert.True(scope.Mobile.IsPairingInProgress);
        // Re-tapping while the first attempt is still out would redeem the
        // same single-use code twice.
        Assert.False(scope.Mobile.IsConfirmPairServerEnabled);
    }

    [AvaloniaFact]
    public async Task A_refused_code_leaves_the_sheet_up_saying_why()
    {
        using var scope = Build();
        OpenSheetFor(scope.Mobile, UnreachableHeadlessServer(), "ABCD2345");

        scope.Mobile.ConfirmPairServerCommand.Execute(null);
        Assert.True(await WaitFor(() => !scope.Mobile.IsPairingInProgress), "the redeem never settled");

        Assert.True(scope.Mobile.IsShowingConfirmPairServer);
        Assert.False(string.IsNullOrEmpty(scope.Mobile.Main.PairingCodeError));
        // The typed code survives, so a mistyped one is corrected rather than
        // retyped from scratch.
        Assert.Equal("ABCD2345", scope.Mobile.PendingPairingCode);
    }

    [AvaloniaFact]
    public void Editing_the_code_clears_the_previous_attempt_s_message()
    {
        using var scope = Build();
        OpenSheetFor(scope.Mobile, UnreachableHeadlessServer(), "ABCD2345");
        scope.Mobile.Main.PairingCodeError = "Invalid, expired, or already-used pairing code.";

        scope.Mobile.PendingPairingCode = "ABCD2346";

        Assert.Null(scope.Mobile.Main.PairingCodeError);
    }

    [AvaloniaFact]
    public void Cancelling_takes_the_code_and_its_message_with_it()
    {
        using var scope = Build();
        OpenSheetFor(scope.Mobile, UnreachableHeadlessServer(), "ABCD2345");
        scope.Mobile.Main.PairingCodeError = "Invalid, expired, or already-used pairing code.";

        scope.Mobile.CancelPairServerCommand.Execute(null);

        Assert.False(scope.Mobile.IsShowingConfirmPairServer);
        Assert.Equal("", scope.Mobile.PendingPairingCode);
        Assert.Null(scope.Mobile.Main.PairingCodeError);
    }

    // The pairing sheet takes Settings' place while it is up (one sheet at a
    // time), so resolving it has to put Settings back rather than leave the
    // user standing in the library - tapping one row inside Settings should not
    // close Settings.
    [AvaloniaFact]
    public void Cancelling_goes_back_to_the_settings_sheet_it_came_from()
    {
        using var scope = Build();
        scope.Mobile.OpenSettingsCommand.Execute(null);
        OpenSheetFor(scope.Mobile, UnreachableHeadlessServer(), "ABCD2345");
        Assert.True(scope.Mobile.IsShowingConfirmPairServer);

        scope.Mobile.CancelPairServerCommand.Execute(null);

        Assert.True(scope.Mobile.IsShowingSettings);
    }

    // Awaited rather than spun on - see MobileSharedPlaybackTests.WaitFor.
    private static async Task<bool> WaitFor(Func<bool> condition)
    {
        for (var i = 0; i < 200; i++)
        {
            if (condition())
                return true;
            await Task.Delay(10);
        }
        return condition();
    }
}
